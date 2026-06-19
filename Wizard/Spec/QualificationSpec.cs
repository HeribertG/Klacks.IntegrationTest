using Klacks.Api.Domain.Enums;

namespace Klacks.IntegrationTest.Wizard.Spec;

/// <summary>
/// Future-axis: a qualification with its per-shift requirement and per-client holdings.
/// The first coverage test leaves the qualification list empty (no qual gating).
/// </summary>
/// <param name="Name">Qualification name.</param>
/// <param name="MandatoryForShiftAbbrevs">Shift abbreviations that mandatorily require it.</param>
/// <param name="MinLevel">Minimum level the shift requires.</param>
/// <param name="HeldByClientIndexes">Client indexes that hold the qualification (at HeldLevel).</param>
/// <param name="HeldLevel">Level the listed clients hold.</param>
public sealed record QualificationSpec(
    string Name,
    IReadOnlyList<string> MandatoryForShiftAbbrevs,
    QualificationLevel MinLevel,
    IReadOnlyList<int> HeldByClientIndexes,
    QualificationLevel HeldLevel);
