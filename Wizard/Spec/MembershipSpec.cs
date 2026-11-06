// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.IntegrationTest.Wizard.Spec;

/// <summary>
/// Membership axis: one entry seeds a single company membership row for a client (entry and exit
/// are inclusive, like Membership.ValidFrom / ValidUntil). Seed at most one entry per client: the
/// production window query reads one row per client.
/// </summary>
/// <param name="ClientIndex">Index of the client the membership belongs to.</param>
/// <param name="ValidFrom">First member day (entry).</param>
/// <param name="ValidUntil">Last member day (exit, inclusive); null = open-ended.</param>
public sealed record MembershipSpec(
    int ClientIndex,
    DateOnly ValidFrom,
    DateOnly? ValidUntil);
