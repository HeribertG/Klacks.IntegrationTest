namespace Klacks.IntegrationTest.Wizard.Spec;

/// <summary>
/// Declarative definition of one shift in the pool. Weekday flags index 0=Monday..6=Sunday.
/// </summary>
/// <param name="Abbrev">Abbreviation/name (e.g. "FD").</param>
/// <param name="StartShift">Shift start time.</param>
/// <param name="EndShift">Shift end time.</param>
/// <param name="WorkTime">Paid hours for the shift.</param>
/// <param name="Quantity">Slots per active day (each expands to one CoreShift with RequiredAssignments=1).</param>
/// <param name="Weekdays">Seven booleans Mon..Sun gating which days the shift is active.</param>
/// <param name="CuttingAfterMidnight">True for shifts that cross midnight (e.g. night shift).</param>
public sealed record ShiftDef(
    string Abbrev,
    TimeOnly StartShift,
    TimeOnly EndShift,
    decimal WorkTime,
    int Quantity,
    bool[] Weekdays,
    bool CuttingAfterMidnight);
