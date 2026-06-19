namespace Klacks.IntegrationTest.Wizard.Spec;

/// <summary>
/// Availability axis: one entry seeds a single client_availability row with IsAvailable=false at
/// the shift's StartShift.Hour, making the (client, shift, date) cell ineligible for the GA. The
/// first coverage test leaves the unavailability list empty (no row = available by default).
/// </summary>
/// <param name="ClientIndex">Index of the client that is unavailable.</param>
/// <param name="Date">The calendar date on which the client is unavailable.</param>
/// <param name="ShiftAbbrev">Abbreviation of the shift the client cannot be assigned to (its start hour is blocked).</param>
public sealed record UnavailabilitySpec(
    int ClientIndex,
    DateOnly Date,
    string ShiftAbbrev);
