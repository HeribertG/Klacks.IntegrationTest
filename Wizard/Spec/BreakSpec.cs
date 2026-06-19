namespace Klacks.IntegrationTest.Wizard.Spec;

/// <summary>
/// Future-axis: an absence/break blocking a cell. The first coverage test leaves the break
/// list empty.
/// </summary>
/// <param name="ClientIndex">Index of the client the break belongs to.</param>
/// <param name="Date">The calendar date.</param>
/// <param name="WorkTime">Paid hours the break contributes toward the target.</param>
public sealed record BreakSpec(
    int ClientIndex,
    DateOnly Date,
    decimal WorkTime);
