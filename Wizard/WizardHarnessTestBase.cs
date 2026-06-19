using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.IntegrationTest.SignalR;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.Wizard;

/// <summary>
/// Abstract NUnit base for the wizard-coverage integration tests. De-duplicates the copy-paste
/// shared by WizardE2ETests and WorkNotificationHubTests: it owns the in-process
/// SignalRTestWebApplicationFactory (OneTimeSetUp), builds a manual DataBaseContext per test, and
/// offers a scope helper plus a build-context-and-run helper for the in-process GA path.
/// Connection string defaults to the integration DB on port 5434, overridable via DATABASE_URL.
/// </summary>
public abstract class WizardHarnessTestBase
{
    protected SignalRTestWebApplicationFactory Factory = null!;
    protected DataBaseContext Context = null!;
    protected string ConnectionString = null!;

    [OneTimeSetUp]
    public void HarnessOneTimeSetUp()
    {
        ConnectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        Factory = new SignalRTestWebApplicationFactory();
    }

    [OneTimeTearDown]
    public void HarnessOneTimeTearDown()
    {
        Factory?.Dispose();
    }

    [SetUp]
    public void HarnessSetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        Context = new DataBaseContext(options, mockHttpContextAccessor);
    }

    [TearDown]
    public void HarnessTearDown()
    {
        Context?.Dispose();
    }

    /// <summary>Creates a fresh DI scope from the in-process server.</summary>
    protected IServiceScope CreateScope() => Factory.Services.CreateScope();

    /// <summary>
    /// Resolves the production context builder, builds the CoreWizardContext for the given request,
    /// then runs the deterministic in-process GA once. Returns BOTH the solved scenario and the
    /// built context so tests can re-run the loop on the same context for determinism checks.
    /// </summary>
    protected async Task<(CoreScenario best, CoreWizardContext context)> BuildContextAndRunAsync(
        WizardContextRequest request,
        TokenEvolutionConfig config,
        CancellationToken ct = default)
    {
        var context = await BuildContextAsync(request, ct);
        var best = RunLoop(context, config);
        return (best, context);
    }

    /// <summary>Resolves the production context builder and builds a CoreWizardContext.</summary>
    protected async Task<CoreWizardContext> BuildContextAsync(
        WizardContextRequest request,
        CancellationToken ct = default)
    {
        using var scope = CreateScope();
        var builder = scope.ServiceProvider.GetRequiredService<IWizardContextBuilder>();
        return await builder.BuildContextAsync(request, ct);
    }

    /// <summary>Runs the deterministic in-process token-evolution loop.</summary>
    protected static CoreScenario RunLoop(CoreWizardContext context, TokenEvolutionConfig config)
        => TokenEvolutionLoop.Create().Run(context, config);
}
