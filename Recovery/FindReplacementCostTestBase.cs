// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Base of the fixtures that look at what one find_replacement search costs against PostgreSQL. It owns the
/// in-process host whose contexts report their SQL commands (SqlCountingTestWebApplicationFactory), a context built
/// by hand for seeding and cleanup (its commands are not counted), and the seed of the fixture's scope: purged before
/// every test and after it, scenario rows of a cover by their token first. Every request runs in a scope of its own,
/// like one HTTP request.
/// </summary>
/// <param name="Scope">Label of the fixture inside the INTEGRATION_TEST_FRM_ prefix, so two fixtures never purge each other's rows</param>

using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Queries.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.Recovery;

public abstract class FindReplacementCostTestBase
{
    private readonly List<Guid> _scenarioTokens = [];

    protected SqlCountingTestWebApplicationFactory Factory { get; private set; } = null!;

    protected DataBaseContext Context { get; private set; } = null!;

    protected abstract string Scope { get; }

    [OneTimeSetUp]
    public void CostOneTimeSetUp()
    {
        Factory = new SqlCountingTestWebApplicationFactory();
    }

    [OneTimeTearDown]
    public void CostOneTimeTearDown()
    {
        Factory?.Dispose();
    }

    [SetUp]
    public async Task CostSetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(TestHostDatabase.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        Context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _scenarioTokens.Clear();
        await FindReplacementPoolSeed.PurgeAsync(Context, Scope);
    }

    [TearDown]
    public async Task CostTearDown()
    {
        if (Context is null)
        {
            return;
        }

        try
        {
            await PurgeSeedAsync();
        }
        finally
        {
            await Context.DisposeAsync();
        }
    }

    protected Task<FindReplacementPoolSeed> SeedAsync(int groupPeople, IReadOnlyList<int> sisterGroupSizes)
        => FindReplacementPoolSeed.CreateAsync(Context, Scope, groupPeople, sisterGroupSizes);

    protected async Task PurgeSeedAsync()
    {
        foreach (var token in _scenarioTokens)
        {
            await FindReplacementPoolSeed.PurgeScenarioAsync(Context, token);
        }

        _scenarioTokens.Clear();
        await FindReplacementPoolSeed.PurgeAsync(Context, Scope);
    }

    protected async Task<CoverAbsenceOutcome> CoverTheSlotHolderAsync(FindReplacementPoolSeed seed)
    {
        var outcome = await SendAsync(new CoverAbsenceCommand(
            seed.SlotHolderId, FindReplacementPoolSeed.Day, seed.GroupId, seed.SickAbsenceId,
            NotifyEscalationRoster: false, Source: ReplacementRequestSource.PlannerDialog));
        _scenarioTokens.Add(outcome.Token);
        return outcome;
    }

    protected async Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request)
    {
        using var scope = Factory.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        return await mediator.Send(request, CancellationToken.None);
    }

    protected static FindReplacementQuery SearchFor(FindReplacementPoolSeed seed, Guid? analyseToken) => new(
        ShiftId: seed.ShiftId,
        Date: FindReplacementPoolSeed.Day,
        StartTime: FindReplacementPoolSeed.ShiftStart,
        EndTime: FindReplacementPoolSeed.ShiftEnd,
        GroupId: seed.GroupId,
        AnalyseToken: analyseToken);
}
