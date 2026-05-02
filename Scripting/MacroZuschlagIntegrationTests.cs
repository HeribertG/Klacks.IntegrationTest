using Shouldly;
using Klacks.Api.Infrastructure.Scripting;
using NUnit.Framework;

namespace Klacks.IntegrationTest.Scripting;

[TestFixture]
public class MacroZuschlagIntegrationTests
{
    private const string ZuschlagMacroScript = @"
import hour
import fromhour
import untilhour
import weekday
import holiday
import holidaynextday
import nightrate
import holidayrate
import sarate
import sorate
import guaranteedhours
import fulltime

FUNCTION CalcSegment(StartTime, EndTime, HolidayFlag, WeekdayNum)
      DIM SegmentHours, NightHours, NonNightHours
      DIM NRate, DRate, HasHoliday, IsSaturday, IsSunday

      SegmentHours = TimeToHours(EndTime) - TimeToHours(StartTime)
      IF SegmentHours < 0 THEN SegmentHours = SegmentHours + 24 ENDIF

      NightHours = TimeOverlap(""23:00"", ""06:00"", StartTime, EndTime)
      NonNightHours = SegmentHours - NightHours

      HasHoliday = HolidayFlag = 1
      IsSaturday = WeekdayNum = 6
      IsSunday = WeekdayNum = 7

      NRate = 0
      IF NightHours > 0 THEN NRate = NightRate ENDIF
      IF HasHoliday AndAlso HolidayRate > NRate THEN NRate = HolidayRate ENDIF
      IF IsSaturday AndAlso SaRate > NRate THEN NRate = SaRate ENDIF
      IF IsSunday AndAlso SoRate > NRate THEN NRate = SoRate ENDIF

      DRate = 0
      IF HasHoliday AndAlso HolidayRate > DRate THEN DRate = HolidayRate ENDIF
      IF IsSaturday AndAlso SaRate > DRate THEN DRate = SaRate ENDIF
      IF IsSunday AndAlso SoRate > DRate THEN DRate = SoRate ENDIF

      CalcSegment = NightHours * NRate + NonNightHours * DRate
  ENDFUNCTION

  DIM TotalBonus, WeekdayNextDay

  WeekdayNextDay = (Weekday MOD 7) + 1

  IF TimeToHours(UntilHour) <= TimeToHours(FromHour) THEN
      TotalBonus = CalcSegment(FromHour, ""00:00"", Holiday, Weekday)
      TotalBonus = TotalBonus + CalcSegment(""00:00"", UntilHour, HolidayNextDay, WeekdayNextDay)
  ELSE
      TotalBonus = CalcSegment(FromHour, UntilHour, Holiday, Weekday)
  ENDIF

  OUTPUT 1, Round(TotalBonus, 2)
";

    [Test]
    public void Compile_ZuschlagMacro_ShouldSucceed()
    {
        // Arrange & Act
        var compiled = CompiledScript.Compile(ZuschlagMacroScript);

        // Assert
        compiled.HasError.ShouldBeFalse($"Compilation failed: {compiled.Error?.Description}");
        compiled.Instructions.Count.ShouldBeGreaterThan(100);
    }

    [Test]
    public void Compile_ZuschlagMacro_ShouldAccept12ExternalVariables()
    {
        // Arrange
        var compiled = CompiledScript.Compile(ZuschlagMacroScript);
        compiled.HasError.ShouldBeFalse();

        // Act - Set all 12 external variables
        SetStandardValues(compiled);

        // Assert - Script should execute successfully with all variables set
        var context = new ScriptExecutionContext(compiled);
        var result = context.Execute();

        result.Success.ShouldBeTrue($"Execution failed: {result.Error?.Description}");
        result.Messages.Count.ShouldBe(1);
    }

    [Test]
    public void Execute_Saturday8Hours_ShouldReturn08Bonus()
    {
        // Arrange
        var compiled = CompiledScript.Compile(ZuschlagMacroScript);
        SetStandardValues(compiled);
        compiled.SetExternalValue("fromhour", "08:00");
        compiled.SetExternalValue("untilhour", "16:00");
        compiled.SetExternalValue("weekday", 6);
        compiled.SetExternalValue("sarate", 0.1m);

        // Act
        var context = new ScriptExecutionContext(compiled);
        var result = context.Execute();

        // Assert
        result.Success.ShouldBeTrue($"Execution failed: {result.Error?.Description}");
        result.Messages.Count.ShouldBe(1);
        decimal.Parse(result.Messages[0].Message).ShouldBe(0.8m);
    }

    [Test]
    public void Execute_Sunday8Hours_ShouldReturnSoRateBonus()
    {
        // Arrange
        var compiled = CompiledScript.Compile(ZuschlagMacroScript);
        SetStandardValues(compiled);
        compiled.SetExternalValue("fromhour", "08:00");
        compiled.SetExternalValue("untilhour", "16:00");
        compiled.SetExternalValue("weekday", 7);
        compiled.SetExternalValue("sorate", 0.15m);

        // Act
        var context = new ScriptExecutionContext(compiled);
        var result = context.Execute();

        // Assert
        result.Success.ShouldBeTrue($"Execution failed: {result.Error?.Description}");
        result.Messages.Count.ShouldBe(1);
        decimal.Parse(result.Messages[0].Message).ShouldBe(1.2m);
    }

    [Test]
    public void Execute_Weekday8Hours_NoBonus_ShouldReturn0()
    {
        // Arrange
        var compiled = CompiledScript.Compile(ZuschlagMacroScript);
        SetStandardValues(compiled);
        compiled.SetExternalValue("fromhour", "08:00");
        compiled.SetExternalValue("untilhour", "16:00");
        compiled.SetExternalValue("weekday", 3);

        // Act
        var context = new ScriptExecutionContext(compiled);
        var result = context.Execute();

        // Assert
        result.Success.ShouldBeTrue($"Execution failed: {result.Error?.Description}");
        result.Messages.Count.ShouldBe(1);
        decimal.Parse(result.Messages[0].Message).ShouldBe(0m);
    }

    [Test]
    public void Execute_Holiday8Hours_ShouldReturnHolidayRateBonus()
    {
        // Arrange
        var compiled = CompiledScript.Compile(ZuschlagMacroScript);
        SetStandardValues(compiled);
        compiled.SetExternalValue("fromhour", "08:00");
        compiled.SetExternalValue("untilhour", "16:00");
        compiled.SetExternalValue("weekday", 3);
        compiled.SetExternalValue("holiday", 1);
        compiled.SetExternalValue("holidayrate", 0.25m);

        // Act
        var context = new ScriptExecutionContext(compiled);
        var result = context.Execute();

        // Assert
        result.Success.ShouldBeTrue($"Execution failed: {result.Error?.Description}");
        result.Messages.Count.ShouldBe(1);
        decimal.Parse(result.Messages[0].Message).ShouldBe(2.0m);
    }

    [Test]
    public void Execute_NightShift_ShouldCalculateNightBonus()
    {
        // Arrange
        var compiled = CompiledScript.Compile(ZuschlagMacroScript);
        SetStandardValues(compiled);
        compiled.SetExternalValue("fromhour", "22:00");
        compiled.SetExternalValue("untilhour", "06:00");
        compiled.SetExternalValue("weekday", 3);
        compiled.SetExternalValue("nightrate", 0.2m);

        // Act
        var context = new ScriptExecutionContext(compiled);
        var result = context.Execute();

        // Assert
        result.Success.ShouldBeTrue($"Execution failed: {result.Error?.Description}");
        result.Messages.Count.ShouldBe(1);
        decimal.Parse(result.Messages[0].Message).ShouldBeGreaterThan(0m);
    }

    [Test]
    public void Execute_NightShiftCrossingMidnight_ShouldSplitCalculation()
    {
        // Arrange
        var compiled = CompiledScript.Compile(ZuschlagMacroScript);
        SetStandardValues(compiled);
        compiled.SetExternalValue("fromhour", "23:00");
        compiled.SetExternalValue("untilhour", "07:00");
        compiled.SetExternalValue("weekday", 6);
        compiled.SetExternalValue("holidaynextday", true);
        compiled.SetExternalValue("sarate", 0.1m);
        compiled.SetExternalValue("holidayrate", 0.25m);
        compiled.SetExternalValue("nightrate", 0.15m);

        // Act
        var context = new ScriptExecutionContext(compiled);
        var result = context.Execute();

        // Assert
        result.Success.ShouldBeTrue($"Execution failed: {result.Error?.Description}");
        result.Messages.Count.ShouldBe(1);
        decimal bonus = decimal.Parse(result.Messages[0].Message);
        bonus.ShouldBeGreaterThan(0m);
    }

    [Test]
    public void Execute_CaseInsensitivity_MixedCaseVariables_ShouldWork()
    {
        // Arrange
        var compiled = CompiledScript.Compile(ZuschlagMacroScript);
        compiled.SetExternalValue("HOUR", 8m);
        compiled.SetExternalValue("FromHour", "08:00");
        compiled.SetExternalValue("UNTILHOUR", "16:00");
        compiled.SetExternalValue("Weekday", 6);
        compiled.SetExternalValue("HOLIDAY", false);
        compiled.SetExternalValue("holidayNextDay", false);
        compiled.SetExternalValue("NightRate", 0.1m);
        compiled.SetExternalValue("HOLIDAYRATE", 0.15m);
        compiled.SetExternalValue("saRate", 0.1m);
        compiled.SetExternalValue("SORATE", 0.1m);
        compiled.SetExternalValue("GuaranteedHours", 160m);
        compiled.SetExternalValue("FullTime", 180m);

        // Act
        var context = new ScriptExecutionContext(compiled);
        var result = context.Execute();

        // Assert
        result.Success.ShouldBeTrue($"Execution failed: {result.Error?.Description}");
        result.Messages.Count.ShouldBe(1);
        decimal.Parse(result.Messages[0].Message).ShouldBe(0.8m);
    }

    [Test]
    public void Execute_HolidayHigherThanSaturday_ShouldUseHolidayRate()
    {
        // Arrange
        var compiled = CompiledScript.Compile(ZuschlagMacroScript);
        SetStandardValues(compiled);
        compiled.SetExternalValue("fromhour", "08:00");
        compiled.SetExternalValue("untilhour", "16:00");
        compiled.SetExternalValue("weekday", 6);
        compiled.SetExternalValue("holiday", 1);
        compiled.SetExternalValue("sarate", 0.1m);
        compiled.SetExternalValue("holidayrate", 0.25m);

        // Act
        var context = new ScriptExecutionContext(compiled);
        var result = context.Execute();

        // Assert
        result.Success.ShouldBeTrue($"Execution failed: {result.Error?.Description}");
        result.Messages.Count.ShouldBe(1);
        decimal.Parse(result.Messages[0].Message).ShouldBe(2.0m);
    }

    [Test]
    public void Execute_WeekdayNextDayCalculation_ShouldWrapCorrectly()
    {
        // Arrange
        var script = @"
import weekday
DIM nextday
nextday = (weekday MOD 7) + 1
OUTPUT 1, nextday
";
        var compiled = CompiledScript.Compile(script);

        // Act & Assert - Sunday (7) -> Monday (1)
        compiled.SetExternalValue("weekday", 7);
        var context1 = new ScriptExecutionContext(compiled);
        var result1 = context1.Execute();
        result1.Messages[0].Message.ShouldBe("1");

        // Act & Assert - Saturday (6) -> Sunday (7)
        compiled.SetExternalValue("weekday", 6);
        var context2 = new ScriptExecutionContext(compiled);
        var result2 = context2.Execute();
        result2.Messages[0].Message.ShouldBe("7");
    }

    private static void SetStandardValues(CompiledScript compiled)
    {
        compiled.SetExternalValue("hour", 8m);
        compiled.SetExternalValue("fromhour", "08:00");
        compiled.SetExternalValue("untilhour", "16:00");
        compiled.SetExternalValue("weekday", 1);
        compiled.SetExternalValue("holiday", 0);
        compiled.SetExternalValue("holidaynextday", 0);
        compiled.SetExternalValue("nightrate", 0m);
        compiled.SetExternalValue("holidayrate", 0m);
        compiled.SetExternalValue("sarate", 0m);
        compiled.SetExternalValue("sorate", 0m);
        compiled.SetExternalValue("guaranteedhours", 160m);
        compiled.SetExternalValue("fulltime", 180m);
    }
}
