using Klacks.Api.Domain.Enums;

namespace Klacks.IntegrationTest.Wizard.Spec;

/// <summary>
/// Future-axis: a pre-existing locked Work the wizard must not touch. The first coverage
/// test leaves the locked list empty.
/// </summary>
/// <param name="ClientIndex">Index of the client the work belongs to.</param>
/// <param name="Date">The calendar date.</param>
/// <param name="ShiftAbbrev">Abbreviation of the shift the locked work occupies.</param>
/// <param name="LockLevel">Lock level (must be != None to count as locked).</param>
public sealed record LockedWorkSpec(
    int ClientIndex,
    DateOnly Date,
    string ShiftAbbrev,
    WorkLockLevel LockLevel);
