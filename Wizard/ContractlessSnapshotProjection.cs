// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using Klacks.ScheduleOptimizer.Models;

namespace Klacks.IntegrationTest.Wizard;

/// <summary>
/// Comparable projection of a Wizard-1 engine snapshot: every CoreAgent serialized as a whole (roster order kept)
/// and every CoreContractDay with its ContractId blanked (it differs by design between a real contract and the
/// settings defaults), compared by record equality so a new contract-day field is compared automatically. Two equal
/// projections mean equal GA inputs, because the optimizer never reads ContractId.
/// </summary>
/// <param name="AgentCount">Number of engine agents</param>
/// <param name="Agents">Serialized agents in roster order</param>
/// <param name="Days">Contract days without ContractId, ordered by agent and date</param>
public sealed record ContractlessSnapshotProjection(int AgentCount, IReadOnlyList<string> Agents, IReadOnlyList<CoreContractDay> Days)
{
    public static ContractlessSnapshotProjection From(CoreWizardContext context) => new(
        context.Agents.Count,
        context.Agents.Select(a => JsonSerializer.Serialize(a)).ToList(),
        context.ContractDays
            .OrderBy(d => d.AgentId, StringComparer.Ordinal)
            .ThenBy(d => d.Date)
            .Select(d => d with { ContractId = Guid.Empty })
            .ToList());
}
