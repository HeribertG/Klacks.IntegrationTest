// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The anonymous first start on real PostgreSQL, with the real CompleteOwnAdminSetupCommandHandler, AdminSetupGateService,
/// UnitOfWork, AdministratorRoleLock, UserManagementService and Identity store. Two first starts run at the same time: the
/// first is held inside its transaction (after its eligibility check, while it holds the administrator-role lock), so the
/// second must wait on the lock, and once the first commits it must get the uniform 409 - exactly one own admin exists, the
/// seeded admin is deactivated and the first start is closed. A seeded admin whose password hash no longer is the seeded
/// one (changed, or rehashed by a sign-in) closes the first start as well. A request that does not come straight from
/// the machine itself needs the configured one-time setup code: without a configured code it is refused, with a wrong
/// code too, with the right one it succeeds. Runs on a throwaway database, because it deactivates the seeded admin that
/// the shared klacks database and every other fixture rely on.
/// </summary>

using Klacks.Api.Application.Commands.Accounts;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Registrations;
using Klacks.Api.Application.Handlers.Accounts;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Services.Authentication;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Application.Exceptions;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Authentication;

[TestFixture]
[Category("RealDatabase")]
[NonParallelizable]
public class FirstStartOwnAdminSetupConcurrencyTests
{
    private const string ThrowawayPrefix = "klacks_boot_";
    private const string NewAdminPassword = "Sup3rSecret!x";
    private const string ChangedPasswordHash = "AQAAAAIAAYagAAAAEChangedOrRehashedAfterTheFirstSignIn==";
    private const string ConfiguredSetupCode = "ABCD-EFGH-JK23";
    private const int WaitMilliseconds = 700;
    private const int FinishSeconds = 30;

    private const string Host = "localhost";
    private const int Port = 5434;
    private const string User = "postgres";
    private const string Password = "admin";

    private string _dbName = null!;
    private ServiceProvider _services = null!;
    private IConfigurationRoot _configuration = null!;

    private static string ConnectionString(string dbName) =>
        $"Host={Host};Port={Port};Database={dbName};Username={User};Password={Password};Pooling=false";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _dbName = ThrowawayPrefix + "firststart_" + Guid.NewGuid().ToString("N")[..8];
        await ExecuteAsync("postgres", $"CREATE DATABASE \"{_dbName}\"");

        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IHttpContextAccessor>());
        _configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        services.AddSingleton<IConfiguration>(_configuration);
        services.AddSingleton(environment);
        services.AddDbContext<DataBaseContext>(options => options
            .UseNpgsql(ConnectionString(_dbName))
            .UseSnakeCaseNamingConvention()
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning)));
        services.AddIdentityCore<AppUser>()
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<DataBaseContext>();
        services.AddScoped<IAppUserReader, AppUserReader>();
        services.AddScoped<IUserManagementService, UserManagementService>();
        services.AddScoped<IUsernameGeneratorService, UsernameGeneratorService>();
        services.AddScoped<IAdminSetupGateService, AdminSetupGateService>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IAdministratorRoleLock, AdministratorRoleLock>();
        services.AddSingleton<AuthMapper>();
        _services = services.BuildServiceProvider();

        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DataBaseContext>().Database.MigrateAsync();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        if (!await roles.RoleExistsAsync(Roles.Admin))
        {
            (await roles.CreateAsync(new IdentityRole(Roles.Admin))).Succeeded.ShouldBeTrue();
        }

        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var seed = new AppUser
        {
            Id = SystemAccounts.SeedAdminUserId,
            UserName = "admin",
            Email = "admin@test.com",
            FirstName = "admin",
            LastName = "admin",
        };
        (await users.CreateAsync(seed)).Succeeded.ShouldBeTrue();
        (await users.AddToRoleAsync(seed, Roles.Admin)).Succeeded.ShouldBeTrue();
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
        _configuration[DeploymentConstants.FirstStartCodeConfigKey] = null;
        await ExecuteAsync(_dbName, $"DELETE FROM \"AspNetUserRoles\" WHERE user_id <> '{SystemAccounts.SeedAdminUserId}'");
        await ExecuteAsync(_dbName, $"UPDATE \"AspNetUsers\" SET deactivated_at = now() WHERE id <> '{SystemAccounts.SeedAdminUserId}'");
        await ExecuteAsync(_dbName,
            $"UPDATE \"AspNetUsers\" SET deactivated_at = NULL, deactivated_by = NULL, password_hash = '{SystemAccounts.SeedAdminInitialPasswordHash}' " +
            $"WHERE id = '{SystemAccounts.SeedAdminUserId}'");
    }

    private static async Task ExecuteAsync(string dbName, string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString(dbName));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static RegistrationResource Registration(string email) => new()
    {
        Email = email,
        FirstName = "Owner",
        LastName = "First",
        Password = NewAdminPassword,
    };

    /// <summary>
    /// Runs one anonymous first start in its own scope; with a gate it is held after the eligibility check, inside the
    /// transaction and under the administrator-role lock.
    /// </summary>
    private async Task CompleteFirstStartAsync(
        string email, PauseGate? gate, bool isLocalRequest = true, string? setupCode = null)
    {
        await using var scope = _services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var usernames = provider.GetRequiredService<IUsernameGeneratorService>();
        if (gate != null)
        {
            var real = usernames;
            usernames = Substitute.For<IUsernameGeneratorService>();
            usernames.GenerateUniqueUsernameAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(async call =>
            {
                var name = await real.GenerateUniqueUsernameAsync(call.ArgAt<string>(0), call.ArgAt<string>(1));
                await gate.PauseAsync();
                return name;
            });
        }

        var handler = new CompleteOwnAdminSetupCommandHandler(
            provider.GetRequiredService<IAdminSetupGateService>(),
            provider.GetRequiredService<IUserManagementService>(),
            provider.GetRequiredService<IAdministratorRoleLock>(),
            usernames,
            provider.GetRequiredService<AuthMapper>(),
            provider.GetRequiredService<IUnitOfWork>(),
            Substitute.For<ILogger<CompleteOwnAdminSetupCommandHandler>>());

        await handler.Handle(
            new CompleteOwnAdminSetupCommand(Registration(email), IsFirstStart: true, isLocalRequest, setupCode),
            CancellationToken.None);
    }

    private async Task<bool> IsFirstStartOpenAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAdminSetupGateService>().IsFirstStartSetupOpenAsync();
    }

    private async Task<(int ActiveAdministrators, bool SeedDeactivated)> StateAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserManagementService>();
        var seed = await users.FindUserByIdNoTrackingAsync(SystemAccounts.SeedAdminUserId);
        return (await users.CountActiveAdministratorsAsync(), seed!.DeactivatedAt != null);
    }

    [Test]
    public async Task TwoSimultaneousFirstStarts_ExactlyOneOwnAdminIsCreated()
    {
        (await IsFirstStartOpenAsync()).ShouldBeTrue();
        var gate = new PauseGate();
        var heldRun = CompleteFirstStartAsync("first-owner@example.invalid", gate);
        Task competingRun;
        try
        {
            await gate.Reached.WaitAsync(TimeSpan.FromSeconds(FinishSeconds));

            competingRun = CompleteFirstStartAsync("second-owner@example.invalid", null);
            await Task.Delay(WaitMilliseconds);
            competingRun.IsCompleted.ShouldBeFalse("The competing first start must wait on the lock while the first transaction is open.");
        }
        finally
        {
            gate.Release();
        }

        await heldRun.WaitAsync(TimeSpan.FromSeconds(FinishSeconds));
        var conflict = await Should.ThrowAsync<ConflictException>(() => competingRun.WaitAsync(TimeSpan.FromSeconds(FinishSeconds)));
        conflict.Message.ShouldBe(AdminSetupMessages.FirstStartUnavailable);

        var (activeAdministrators, seedDeactivated) = await StateAsync();
        activeAdministrators.ShouldBe(1);
        seedDeactivated.ShouldBeTrue();
        (await IsFirstStartOpenAsync()).ShouldBeFalse();
    }

    [Test]
    public async Task SeedPasswordNoLongerTheSeededOne_FirstStartIsClosed()
    {
        await ExecuteAsync(_dbName,
            $"UPDATE \"AspNetUsers\" SET password_hash = '{ChangedPasswordHash}' WHERE id = '{SystemAccounts.SeedAdminUserId}'");

        (await IsFirstStartOpenAsync()).ShouldBeFalse();
        await Should.ThrowAsync<ConflictException>(() => CompleteFirstStartAsync("late-owner@example.invalid", null));

        var (activeAdministrators, seedDeactivated) = await StateAsync();
        activeAdministrators.ShouldBe(1);
        seedDeactivated.ShouldBeFalse();
    }

    [Test]
    public async Task RemoteFirstStartWithoutConfiguredCode_IsRefused()
    {
        await Should.ThrowAsync<ConflictException>(
            () => CompleteFirstStartAsync("remote-owner@example.invalid", null, isLocalRequest: false, setupCode: ConfiguredSetupCode));

        var (activeAdministrators, seedDeactivated) = await StateAsync();
        activeAdministrators.ShouldBe(1);
        seedDeactivated.ShouldBeFalse();
    }

    [Test]
    public async Task RemoteFirstStartWithAWrongCode_IsRefused()
    {
        _configuration[DeploymentConstants.FirstStartCodeConfigKey] = ConfiguredSetupCode;

        var conflict = await Should.ThrowAsync<ConflictException>(
            () => CompleteFirstStartAsync("remote-owner@example.invalid", null, isLocalRequest: false, setupCode: "ABCD-EFGH-JK24"));

        conflict.Message.ShouldBe(AdminSetupMessages.FirstStartUnavailable);
        (await StateAsync()).SeedDeactivated.ShouldBeFalse();
    }

    [Test]
    public async Task RemoteFirstStartWithTheConfiguredCode_CreatesTheOwnAdmin()
    {
        _configuration[DeploymentConstants.FirstStartCodeConfigKey] = ConfiguredSetupCode;

        await CompleteFirstStartAsync("remote-owner@example.invalid", null, isLocalRequest: false, setupCode: "abcd efgh jk23");

        var (activeAdministrators, seedDeactivated) = await StateAsync();
        activeAdministrators.ShouldBe(1);
        seedDeactivated.ShouldBeTrue();
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
