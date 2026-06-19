namespace Klacks.IntegrationTest.Wizard.Spec;

/// <summary>
/// Future-axis: a per-(client,date) schedule command (e.g. "FREE", "-EARLY"). The first
/// coverage test leaves the command list empty.
/// </summary>
/// <param name="ClientIndex">Index of the client the command applies to.</param>
/// <param name="Date">The calendar date.</param>
/// <param name="CommandKeyword">Raw keyword string (FREE / -FREE / EARLY / -EARLY / LATE / -LATE / NIGHT / -NIGHT).</param>
public sealed record ScheduleCommandSpec(
    int ClientIndex,
    DateOnly Date,
    string CommandKeyword);
