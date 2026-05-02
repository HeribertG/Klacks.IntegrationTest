using Shouldly;
using Klacks.Api.Infrastructure.Scripting;
using NUnit.Framework;

namespace Klacks.IntegrationTest.Scripting;

[TestFixture]
public class ScriptingEngineIntegrationTests
{
    [Test]
    public void Compile_SimpleScript_ShouldSucceed()
    {
        // Arrange
        var script = @"
DIM x
x = 10
OUTPUT 1, x
";

        // Act
        var compiled = CompiledScript.Compile(script);

        // Assert
        compiled.HasError.ShouldBeFalse();
        compiled.Instructions.ShouldNotBeEmpty();
    }

    [Test]
    public void Compile_WithImports_ShouldAllowSettingExternalValues()
    {
        // Arrange
        var script = @"
import hour
import weekday
import nightrate

DIM result
result = hour * nightrate
OUTPUT 1, result
";

        // Act
        var compiled = CompiledScript.Compile(script);
        compiled.SetExternalValue("hour", 8m);
        compiled.SetExternalValue("weekday", 1);
        compiled.SetExternalValue("nightrate", 0.1m);

        var context = new ScriptExecutionContext(compiled);
        var result = context.Execute();

        // Assert
        compiled.HasError.ShouldBeFalse();
        result.Success.ShouldBeTrue();
        decimal.Parse(result.Messages[0].Message).ShouldBe(0.8m);
    }

    [Test]
    public void Compile_WithFunction_ShouldSucceed()
    {
        // Arrange
        var script = @"
FUNCTION Double(x)
    Double = x * 2
ENDFUNCTION

DIM result
result = Double(5)
OUTPUT 1, result
";

        // Act
        var compiled = CompiledScript.Compile(script);

        // Assert
        compiled.HasError.ShouldBeFalse();
    }

    [Test]
    public void Compile_SyntaxError_UndefinedFunction_ShouldReturnError()
    {
        // Arrange
        var script = @"
DIM x
x = UndefinedFunction(5)
OUTPUT 1, x
";

        // Act
        var compiled = CompiledScript.Compile(script);

        // Assert - Entweder Compile-Error oder Runtime-Error
        if (!compiled.HasError)
        {
            var context = new ScriptExecutionContext(compiled);
            var result = context.Execute();
            result.Success.ShouldBeFalse();
        }
    }

    [Test]
    public void Execute_SimpleCalculation_ShouldReturnCorrectResult()
    {
        // Arrange
        var script = @"
DIM x, y, result
x = 10
y = 5
result = x + y
OUTPUT 1, result
";
        var compiled = CompiledScript.Compile(script);

        // Act
        var context = new ScriptExecutionContext(compiled);
        var result = context.Execute();

        // Assert
        result.Success.ShouldBeTrue();
        result.Messages.Count.ShouldBe(1);
        result.Messages[0].Type.ShouldBe(1);
        result.Messages[0].Message.ShouldBe("15");
    }

    [Test]
    public void Execute_WithExternalVariables_ShouldUseProvidedValues()
    {
        // Arrange
        var script = @"
import hour
import rate

DIM bonus
bonus = hour * rate
OUTPUT 1, bonus
";
        var compiled = CompiledScript.Compile(script);
        compiled.SetExternalValue("hour", 8m);
        compiled.SetExternalValue("rate", 0.1m);

        // Act
        var context = new ScriptExecutionContext(compiled);
        var result = context.Execute();

        // Assert
        result.Success.ShouldBeTrue();
        result.Messages.Count.ShouldBe(1);
        decimal.Parse(result.Messages[0].Message).ShouldBe(0.8m);
    }

    [Test]
    public void Execute_CaseInsensitive_ImportLowercaseUsePascalCase_ShouldWork()
    {
        // Arrange
        var script = @"
import nightrate
import holidayrate

DIM MaxRate
IF NightRate > HolidayRate THEN
    MaxRate = NightRate
ELSE
    MaxRate = HolidayRate
ENDIF
OUTPUT 1, MaxRate
";
        var compiled = CompiledScript.Compile(script);
        compiled.SetExternalValue("nightrate", 0.15m);
        compiled.SetExternalValue("holidayrate", 0.10m);

        // Act
        var context = new ScriptExecutionContext(compiled);
        var result = context.Execute();

        // Assert
        result.Success.ShouldBeTrue();
        result.Messages.Count.ShouldBe(1);
        decimal.Parse(result.Messages[0].Message).ShouldBe(0.15m);
    }

    [Test]
    public void Execute_CaseInsensitive_ImportPascalCaseUseLowercase_ShouldWork()
    {
        // Arrange
        var script = @"
import NightRate
import HolidayRate

DIM maxrate
IF nightrate > holidayrate THEN
    maxrate = nightrate
ELSE
    maxrate = holidayrate
ENDIF
OUTPUT 1, maxrate
";
        var compiled = CompiledScript.Compile(script);
        compiled.SetExternalValue("NightRate", 0.05m);
        compiled.SetExternalValue("HolidayRate", 0.20m);

        // Act
        var context = new ScriptExecutionContext(compiled);
        var result = context.Execute();

        // Assert
        result.Success.ShouldBeTrue();
        result.Messages.Count.ShouldBe(1);
        decimal.Parse(result.Messages[0].Message).ShouldBe(0.20m);
    }

    [Test]
    public void Execute_TimeToHours_ShouldConvertStringToDecimal()
    {
        // Arrange
        var script = @"
import fromhour
import untilhour

DIM duration
duration = TimeToHours(untilhour) - TimeToHours(fromhour)
OUTPUT 1, duration
";
        var compiled = CompiledScript.Compile(script);
        compiled.SetExternalValue("fromhour", "08:00");
        compiled.SetExternalValue("untilhour", "16:30");

        // Act
        var context = new ScriptExecutionContext(compiled);
        var result = context.Execute();

        // Assert
        result.Success.ShouldBeTrue();
        result.Messages.Count.ShouldBe(1);
        decimal.Parse(result.Messages[0].Message).ShouldBe(8.5m);
    }

    [Test]
    public void Execute_TimeOverlap_NightShift_ShouldCalculateCorrectly()
    {
        // Arrange
        var script = @"
import fromhour
import untilhour

DIM nighthours
nighthours = TimeOverlap(""23:00"", ""06:00"", fromhour, untilhour)
OUTPUT 1, nighthours
";
        var compiled = CompiledScript.Compile(script);
        compiled.SetExternalValue("fromhour", "22:00");
        compiled.SetExternalValue("untilhour", "07:00");

        // Act
        var context = new ScriptExecutionContext(compiled);
        var result = context.Execute();

        // Assert
        result.Success.ShouldBeTrue();
        result.Messages.Count.ShouldBe(1);
        decimal.Parse(result.Messages[0].Message).ShouldBe(7m);
    }

    [Test]
    public void Execute_IfElseCondition_ShouldEvaluateCorrectly()
    {
        // Arrange
        var script = @"
import weekday

DIM isWeekend
IF weekday = 6 OrElse weekday = 7 THEN
    isWeekend = 1
ELSE
    isWeekend = 0
ENDIF
OUTPUT 1, isWeekend
";
        var compiled = CompiledScript.Compile(script);
        compiled.SetExternalValue("weekday", 6);

        // Act
        var context = new ScriptExecutionContext(compiled);
        var result = context.Execute();

        // Assert
        result.Success.ShouldBeTrue();
        result.Messages.Count.ShouldBe(1);
        result.Messages[0].Message.ShouldBe("1");
    }

    [Test]
    public void Execute_SelectCase_ShouldMatchCorrectCase()
    {
        // Arrange - Simple SELECT CASE without IMPORT
        var script = @"
DIM weekday
DIM daycode
weekday = 6
daycode = 0
SELECT CASE weekday
    CASE 1
        daycode = 100
    CASE 6
        daycode = 600
    CASE ELSE
        daycode = 999
END SELECT
OUTPUT 1, daycode
";
        var compiled = CompiledScript.Compile(script);

        Console.WriteLine($"SELECT CASE HasError: {compiled.HasError}");
        if (compiled.HasError)
        {
            Console.WriteLine($"Compile Error: {compiled.Error?.Description} at line {compiled.Error?.Line}");
        }
        compiled.HasError.ShouldBeFalse($"Compilation failed: {compiled.Error?.Description}");

        var context = new ScriptExecutionContext(compiled);
        var result = context.Execute();

        Console.WriteLine($"SELECT CASE Execution Success: {result.Success}");
        if (!result.Success)
        {
            Console.WriteLine($"Runtime Error: {result.Error?.Description} at line {result.Error?.Line}");
        }
        Console.WriteLine($"Messages count: {result.Messages.Count}");
        foreach (var msg in result.Messages)
        {
            Console.WriteLine($"  Output: Type={msg.Type}, Value={msg.Message}");
        }

        result.Success.ShouldBeTrue($"Execution failed: {result.Error?.Description}");
        result.Messages.Count.ShouldBe(1);
        result.Messages[0].Message.ShouldBe("600");
    }

    [Test]
    public void Execute_RoundFunction_ShouldRoundCorrectly()
    {
        // Arrange
        var script = @"
DIM value, rounded
value = 3.14159
rounded = Round(value, 2)
OUTPUT 1, rounded
";
        var compiled = CompiledScript.Compile(script);

        // Act
        var context = new ScriptExecutionContext(compiled);
        var result = context.Execute();

        // Assert
        result.Success.ShouldBeTrue();
        result.Messages.Count.ShouldBe(1);
        decimal.Parse(result.Messages[0].Message).ShouldBe(3.14m);
    }

    [Test]
    public void Execute_IntegerAsBoolean_ShouldHandleCorrectly()
    {
        // Arrange
        var script = @"
import holiday

DIM bonus
IF holiday = 1 THEN
    bonus = 100
ELSE
    bonus = 0
ENDIF
OUTPUT 1, bonus
";
        var compiled = CompiledScript.Compile(script);
        compiled.SetExternalValue("holiday", 1);

        // Act
        var context = new ScriptExecutionContext(compiled);
        var result = context.Execute();

        // Assert
        result.Success.ShouldBeTrue();
        result.Messages.Count.ShouldBe(1);
        result.Messages[0].Message.ShouldBe("100");
    }

    [Test]
    public void Execute_ModOperator_ShouldCalculateRemainder()
    {
        // Arrange
        var script = @"
import weekday

DIM nextday
nextday = (weekday MOD 7) + 1
OUTPUT 1, nextday
";
        var compiled = CompiledScript.Compile(script);
        compiled.SetExternalValue("weekday", 7);

        // Act
        var context = new ScriptExecutionContext(compiled);
        var result = context.Execute();

        // Assert
        result.Success.ShouldBeTrue();
        result.Messages.Count.ShouldBe(1);
        result.Messages[0].Message.ShouldBe("1");
    }

    [Test]
    public void Execute_MultipleOutputs_ShouldCollectAll()
    {
        // Arrange
        var script = @"
OUTPUT 1, 100
OUTPUT 2, 200
OUTPUT 3, 300
";
        var compiled = CompiledScript.Compile(script);

        // Act
        var context = new ScriptExecutionContext(compiled);
        var result = context.Execute();

        // Assert
        result.Success.ShouldBeTrue();
        result.Messages.Count.ShouldBe(3);
        result.Messages[0].Type.ShouldBe(1);
        result.Messages[0].Message.ShouldBe("100");
        result.Messages[1].Type.ShouldBe(2);
        result.Messages[1].Message.ShouldBe("200");
        result.Messages[2].Type.ShouldBe(3);
        result.Messages[2].Message.ShouldBe("300");
    }
}
