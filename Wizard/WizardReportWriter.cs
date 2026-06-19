using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace Klacks.IntegrationTest.Wizard;

/// <summary>
/// Writes a WizardAnalysisResult to the test output directory as an indented JSON artifact and
/// echoes a readable summary block (scale, config, hard-assert metrics, report-only metrics,
/// per-client table, violations) to TestContext.Out. Reused verbatim by every coverage test.
/// </summary>
public static class WizardReportWriter
{
    public static string Write(WizardAnalysisResult result)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            $"wizard-coverage-{result.ScenarioName}-{timestamp}.json");

        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        File.WriteAllText(path, json, Encoding.UTF8);

        WriteSummary(result, path);
        return path;
    }

    private static void WriteSummary(WizardAnalysisResult result, string path)
    {
        var m = result.Metrics;
        var s = result.Scale;
        var c = result.EffectiveConfig;

        TestContext.Out.WriteLine("==== Wizard Coverage Report ====");
        TestContext.Out.WriteLine($"Scenario : {result.ScenarioName}");
        TestContext.Out.WriteLine($"Artifact : {path}");
        TestContext.Out.WriteLine(
            $"Scale    : clients={s.Clients} shifts={s.Shifts} slots={s.Slots} periodDays={s.PeriodDays}");
        TestContext.Out.WriteLine(
            $"Config   : population={c.PopulationSize} maxGenerations={c.MaxGenerations} seed={c.RandomSeed}");

        TestContext.Out.WriteLine("-- HARD-asserted metrics --");
        TestContext.Out.WriteLine($"  Stage0                 = {m.Stage0}");
        TestContext.Out.WriteLine($"  TheoreticalMaxCoverage = {m.TheoreticalMaxCoverage:0.####}");
        TestContext.Out.WriteLine($"  CoveragePercent        = {m.CoveragePercent:0.####}");
        TestContext.Out.WriteLine($"  UndersupplyCount       = {m.UndersupplyCount}");
        TestContext.Out.WriteLine($"  OverstaffingCount      = {m.OverstaffingCount}");

        TestContext.Out.WriteLine("-- REPORT-only metrics --");
        TestContext.Out.WriteLine($"  ShiftTypeEntropyAvg         = {m.ShiftTypeEntropyAvg:0.####}");
        TestContext.Out.WriteLine($"  SlotGini                    = {m.SlotGini:0.####}");
        TestContext.Out.WriteLine($"  RosterFidelityInversionRate = {m.RosterFidelityInversionRate:0.####}");
        TestContext.Out.WriteLine($"  TargetReachedPercent        = {m.TargetReachedPercent:0.####}");
        TestContext.Out.WriteLine($"  QualificationViolations     = {m.QualificationViolations}");
        TestContext.Out.WriteLine($"  CommandViolations           = {m.CommandViolations}");
        TestContext.Out.WriteLine($"  DurationMs                  = {m.DurationMs}");

        TestContext.Out.WriteLine("-- violationsByKind --");
        if (m.ViolationsByKind.Count == 0)
        {
            TestContext.Out.WriteLine("  (none)");
        }
        else
        {
            foreach (var kv in m.ViolationsByKind)
            {
                TestContext.Out.WriteLine($"  {kv.Key} = {kv.Value}");
            }
        }

        TestContext.Out.WriteLine("-- per-client --");
        TestContext.Out.WriteLine("  idx | slots | hours | target | deviation | FD/SD/ND");
        foreach (var p in result.PerClient)
        {
            TestContext.Out.WriteLine(
                $"  {p.ClientIndex,3} | {p.Slots,5} | {p.Hours,5:0.#} | {p.Target,6:0.#} | "
                + $"{p.Deviation,9:0.#} | {p.ShiftTypeMix[0]}/{p.ShiftTypeMix[1]}/{p.ShiftTypeMix[2]}");
        }

        TestContext.Out.WriteLine($"-- uncovered slots: {result.Uncovered.Count} --");
        TestContext.Out.WriteLine("================================");
    }
}
