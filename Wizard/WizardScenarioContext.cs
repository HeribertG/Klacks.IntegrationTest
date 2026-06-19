using Klacks.Api.Application.Interfaces.Schedules;

namespace Klacks.IntegrationTest.Wizard;

/// <summary>
/// The seeded-state handle returned by <see cref="WizardScenarioBuilder.SeedAsync"/>: the
/// deterministic agent ids (in client order), the shift ids and a ready-to-run context request.
/// </summary>
/// <param name="AgentIds">Seeded Client ids in client index order (0..ClientCount-1).</param>
/// <param name="ShiftIds">Seeded Shift ids in shift-def order.</param>
/// <param name="ContextRequest">The WizardContextRequest covering the seeded scenario.</param>
public sealed record WizardScenarioContext(
    IReadOnlyList<Guid> AgentIds,
    IReadOnlyList<Guid> ShiftIds,
    WizardContextRequest ContextRequest);
