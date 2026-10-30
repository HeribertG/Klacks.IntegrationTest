// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The last-administrator protection under simultaneous requests on real PostgreSQL: two scopes each remove one of the two
/// administrators with the real command handlers, UnitOfWork, AdministratorRoleLock, UserManagementService and Identity store.
/// The first handler is held inside its transaction after its change, so the interleaving is forced: the second handler must
/// wait on the lock instead of finishing, and once the first commits it must be refused, so exactly one administrator stays.
/// Without the lock the second handler counts two administrators, finishes at once and both changes commit. Outside a
/// transaction the lock refuses. This runs on a throwaway database, not on the shared klacks: the rule only bites at exactly
/// two active administrators, klacks holds the real admin account plus leftovers of other fixtures, and a test must not touch
/// rows without the INTEGRATION_TEST_ prefix.
/// </summary>

using Klacks.Api.Application.Commands.Accounts;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Accounts;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Authentication;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.DTOs;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Services.Accounts;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Authentification;
using Klacks.Api.Infrastructure.Services.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Authentication;

[TestFixture]
[Category("RealDatabase")]
[NonParallelizable]
public class AdministratorRoleLockConcurrencyTests
{
    private const string ThrowawayPrefix = "klacks_boot_";
    private const string UserPrefix = "INTEGRATION_TEST_ROLELOCK_";
    private const int WaitMilliseconds = 700;
    private const int FinishSeconds = 30;

    private const string Host = "localhost";
    private const int Port = 5434;
    private const string User = "postgres";
    private const string Password = "admin";

    private string _dbName = null!;
    private ServiceProvider _services = null!;
    private AppUser _first = null!;
    private AppUser _second = null!;

    private static string ConnectionString(string dbName) =>
        $"Host={Host};Port={Port};Database={dbName};Username={User};Password={Password};Pooling=false";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbName = ThrowawayPrefix + "rolelock_" + Guid.NewGuid().ToString("N")[..8];
        await ExecuteAsync("postgres", $"CREATE DATABASE \"{_dbName}\"");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IHttpContextAccessor>());
        services.AddDbContext<DataBaseContext>(options => options
            .UseNpgsql(ConnectionString(_dbName))
            .UseSnakeCaseNamingConvention()
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning)));
        services.AddIdentityCore<AppUser>()
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<DataBaseContext>();
        services.AddScoped<IAppUserReader, AppUserReader>();
        services.AddScoped<IUserManagementService, UserManagementService>();
        services.AddScoped(_ => Substitute.For<IAppUserRepository>());
        services.AddScoped<AccountManagementService>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IAdministratorRoleLock, AdministratorRoleLock>();
        _services = services.BuildServiceProvider();

        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DataBaseContext>().Database.MigrateAsync();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        if (!await roles.RoleExistsAsync(Roles.Admin))
        {
            (await roles.CreateAsync(new IdentityRole(Roles.Admin))).Succeeded.ShouldBeTrue();
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await _services.DisposeAsync();
        _dbName.ShouldStartWith(ThrowawayPrefix);
        await ExecuteAsync("postgres", $"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");
    }

    [SetUp]
    public async Task SetUp()
    {
        await ExecuteAsync(_dbName, "DELETE FROM \"AspNetUserRoles\"");
        await ExecuteAsync(_dbName, "UPDATE \"AspNetUsers\" SET deactivated_at = now()");
        _first = await CreateAdministratorAsync();
        _second = await CreateAdministratorAsync();
    }

    private static async Task ExecuteAsync(string dbName, string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString(dbName));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<AppUser> CreateAdministratorAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var name = UserPrefix + Guid.NewGuid().ToString("N");
        var user = new AppUser { UserName = name, Email = name + "@example.invalid", FirstName = "Rolle", LastName = "Test" };
        (await users.CreateAsync(user)).Succeeded.ShouldBeTrue();
        (await users.AddToRoleAsync(user, Roles.Admin)).Succeeded.ShouldBeTrue();
        return user;
    }

    private async Task<int> ActiveAdministratorsAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IUserManagementService>().CountActiveAdministratorsAsync();
    }

    /// <summary>
    /// The real account service for one scope; with a gate it holds the caller inside the transaction after the change is made.
    /// </summary>
    private static IAccountManagementService AccountService(IServiceProvider provider, PauseGate? gate)
    {
        var real = provider.GetRequiredService<AccountManagementService>();
        if (gate == null)
        {
            return real;
        }

        var paused = Substitute.For<IAccountManagementService>();
        paused.ChangeRoleUserAsync(Arg.Any<ChangeRole>()).Returns(async call =>
        {
            var result = await real.ChangeRoleUserAsync(call.Arg<ChangeRole>());
            await gate.PauseAsync();
            return result;
        });
        paused.DeactivateAccountUserAsync(Arg.Any<Guid>(), Arg.Any<string>()).Returns(async call =>
        {
            var result = await real.DeactivateAccountUserAsync(call.Arg<Guid>(), call.Arg<string>());
            await gate.PauseAsync();
            return result;
        });
        return paused;
    }

    private async Task DemoteAsync(AppUser user, PauseGate? gate)
    {
        await using var scope = _services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var handler = new ChangeRoleCommandHandler(
            AccountService(provider, gate),
            provider.GetRequiredService<IAdministratorRoleLock>(),
            provider.GetRequiredService<IUnitOfWork>(),
            Substitute.For<ILogger<ChangeRoleCommandHandler>>());

        await handler.Handle(
            new ChangeRoleCommand(new ChangeRole { UserId = user.Id, RoleName = Roles.Admin, IsSelected = false }),
            CancellationToken.None);
    }

    private async Task DeactivateAsync(AppUser user, PauseGate? gate)
    {
        await using var scope = _services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var handler = new DeactivateAccountCommandHandler(
            AccountService(provider, gate),
            provider.GetRequiredService<IAdministratorRoleLock>(),
            provider.GetRequiredService<IHttpContextAccessor>(),
            provider.GetRequiredService<IUnitOfWork>(),
            Substitute.For<ILogger<DeactivateAccountCommandHandler>>());

        await handler.Handle(new DeactivateAccountCommand(Guid.Parse(user.Id)), CancellationToken.None);
    }

    private static async Task ExactlyTheHeldOneSucceedsAsync(
        Func<PauseGate, Task> held, Func<Task> competing)
    {
        var gate = new PauseGate();
        var heldRun = held(gate);
        Task competingRun;
        try
        {
            await gate.Reached.WaitAsync(TimeSpan.FromSeconds(FinishSeconds));

            competingRun = competing();
            await Task.Delay(WaitMilliseconds);
            competingRun.IsCompleted.ShouldBeFalse("The competing request must wait on the lock while the first transaction is open.");
        }
        finally
        {
            gate.Release();
        }

        await heldRun.WaitAsync(TimeSpan.FromSeconds(FinishSeconds));
        await Should.ThrowAsync<ConflictException>(() => competingRun.WaitAsync(TimeSpan.FromSeconds(FinishSeconds)));
    }

    [Test]
    public async Task TwoSimultaneousDemotions_ExactlyOneSucceeds()
    {
        await ExactlyTheHeldOneSucceedsAsync(gate => DemoteAsync(_first, gate), () => DemoteAsync(_second, null));

        (await ActiveAdministratorsAsync()).ShouldBe(1);
    }

    [Test]
    public async Task TwoSimultaneousDeactivations_ExactlyOneSucceeds()
    {
        await ExactlyTheHeldOneSucceedsAsync(gate => DeactivateAsync(_first, gate), () => DeactivateAsync(_second, null));

        (await ActiveAdministratorsAsync()).ShouldBe(1);
    }

    [Test]
    public async Task TheLock_RefusesACallOutsideATransaction_AndPassesInsideOne()
    {
        await using var scope = _services.CreateAsyncScope();
        var administratorLock = scope.ServiceProvider.GetRequiredService<IAdministratorRoleLock>();

        await Should.ThrowAsync<InvalidOperationException>(() => administratorLock.AcquireAsync(CancellationToken.None));

        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteInTransactionAsync(async () =>
        {
            await administratorLock.AcquireAsync(CancellationToken.None);
            return true;
        });
    }

    private sealed class PauseGate
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reached => _reached.Task;

        public async Task PauseAsync()
        {
            _reached.TrySetResult();
            await _released.Task;
        }

        public void Release() => _released.TrySetResult();
    }
}
