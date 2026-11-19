// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Integration tests for the LDAP client-sync pipeline (ClientSyncService via SyncClientsCommand),
/// exercised through the real DI-wired command/repository/unit-of-work stack against the real Postgres
/// test database, with ILdapService substituted so no external LDAP server is contacted.
/// Replaces the live-LDAP verification previously only covered by the (skipped) Steps 6-15 of
/// Klacks.E2ETest/Settings/SettingsIdentityProviderTest.cs.
/// </summary>

using Klacks.Api.Application.Commands.IdentityProviders;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Identity;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Authentification;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ClearExtensions;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.IdentityProviders;

[TestFixture]
[Category("RealDatabase")]
public class LdapClientSyncIntegrationTests
{
    private const string TestDataPrefix = "INTEGRATION_TEST_LDAP_";

    private LdapClientSyncTestFactory _factory = null!;
    private string _connectionString = null!;
    private readonly List<Guid> _createdProviderIds = new();
    private readonly List<Guid> _createdClientIds = new();

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        _factory = new LdapClientSyncTestFactory();
        _ = _factory.Services;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _factory?.Dispose();
    }

    [TearDown]
    public async Task TearDown()
    {
        using var context = CreateContext();

        var syncLogs = await context.Set<IdentityProviderSyncLog>()
            .Where(l => _createdProviderIds.Contains(l.IdentityProviderId))
            .ToListAsync();
        context.RemoveRange(syncLogs);

        var memberships = await context.Set<Membership>()
            .Where(m => _createdClientIds.Contains(m.ClientId))
            .ToListAsync();
        context.RemoveRange(memberships);

        var clients = await context.Client
            .IgnoreQueryFilters()
            .Where(c => _createdClientIds.Contains(c.Id) || (c.Name != null && c.Name.StartsWith(TestDataPrefix)))
            .ToListAsync();
        context.RemoveRange(clients);

        var providers = await context.Set<IdentityProvider>()
            .Where(p => _createdProviderIds.Contains(p.Id))
            .ToListAsync();
        context.RemoveRange(providers);

        await context.SaveChangesAsync();

        _createdProviderIds.Clear();
        _createdClientIds.Clear();
        _factory.FakeLdapService.ClearSubstitute();
    }

    private DataBaseContext CreateContext()
    {
        return _factory.Services.CreateScope().ServiceProvider.GetRequiredService<DataBaseContext>();
    }

    private async Task<Guid> CreateTestProviderAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<DataBaseContext>();

        var provider = new IdentityProvider
        {
            Id = Guid.NewGuid(),
            Name = $"{TestDataPrefix}{Guid.NewGuid():N}",
            Type = IdentityProviderType.Ldap,
            IsEnabled = true,
            UseForClientImport = true,
        };

        context.Add(provider);
        await context.SaveChangesAsync();

        _createdProviderIds.Add(provider.Id);
        return provider.Id;
    }

    [Test]
    public async Task SyncClientsAsync_NewLdapUser_CreatesClientMembershipAndSyncLog()
    {
        // Arrange
        var providerId = await CreateTestProviderAsync();
        var externalId = $"{TestDataPrefix}guid-{Guid.NewGuid():N}";

        StubDirectory(providerId, isComplete: true, new LdapUserEntry
        {
            ObjectGuid = externalId,
            DistinguishedName = $"cn={TestDataPrefix}NewUser,dc=example,dc=com",
            GivenName = "IntegrationTest",
            Surname = $"{TestDataPrefix}NewUser",
            IsEnabled = true,
        });

        using var scope = _factory.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        // Act
        var result = await mediator.Send(new SyncClientsCommand(providerId));

        // Assert
        result.Success.ShouldBeTrue(result.ErrorMessage);
        result.NewClients.ShouldBe(1);
        result.TotalProcessed.ShouldBe(1);

        using var context = CreateContext();
        var createdClient = await context.Client
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.LdapExternalId == externalId);

        createdClient.ShouldNotBeNull();
        createdClient!.Name.ShouldBe($"{TestDataPrefix}NewUser");
        createdClient.FirstName.ShouldBe("IntegrationTest");
        createdClient.IdentityProviderId.ShouldBe(providerId);
        _createdClientIds.Add(createdClient.Id);

        var syncLog = await context.Set<IdentityProviderSyncLog>()
            .FirstOrDefaultAsync(l => l.ExternalId == externalId);
        syncLog.ShouldNotBeNull();
        syncLog!.ClientId.ShouldBe(createdClient.Id);
        syncLog.IsActiveInSource.ShouldBeTrue();
    }

    [Test]
    public async Task SyncClientsAsync_NewLdapUserWithOnlyAnEntryUuid_IsKeyedOnTheEntryUuid()
    {
        var providerId = await CreateTestProviderAsync();
        var entryUuid = Guid.NewGuid().ToString();

        StubDirectory(providerId, isComplete: true, new LdapUserEntry
        {
            EntryUuid = entryUuid,
            DistinguishedName = $"uid={TestDataPrefix}Uuid,ou=people,dc=example,dc=com",
            Surname = $"{TestDataPrefix}Uuid",
            IsEnabled = true,
        });

        using var scope = _factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new SyncClientsCommand(providerId));
        var createdId = await FindClientIdByExternalIdAsync(entryUuid);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        createdId.ShouldNotBeNull();
        using var context = CreateContext();
        var syncLog = await context.Set<IdentityProviderSyncLog>().FirstAsync(l => l.ClientId == createdId);
        syncLog.ExternalId.ShouldBe(entryUuid);
    }

    [Test]
    public async Task SyncClientsAsync_UserNoLongerInLdap_DeactivatesMembership()
    {
        var providerId = await CreateTestProviderAsync();
        var leaverExternalId = $"{TestDataPrefix}guid-{Guid.NewGuid():N}";
        var stayerExternalId = $"{TestDataPrefix}guid-{Guid.NewGuid():N}";
        var leaverClientId = await SeedActiveClientAsync(providerId, leaverExternalId, "Leaver");

        // LDAP still returns somebody else, but no longer this user (e.g. left the company).
        StubDirectory(providerId, isComplete: true, new LdapUserEntry
        {
            ObjectGuid = stayerExternalId,
            DistinguishedName = $"cn={TestDataPrefix}Stayer,dc=example,dc=com",
            Surname = $"{TestDataPrefix}Stayer",
            IsEnabled = true,
        });

        using var scope = _factory.Services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new SyncClientsCommand(providerId));
        await FindClientIdByExternalIdAsync(stayerExternalId);

        result.Success.ShouldBeTrue(result.ErrorMessage);
        result.DeactivatedClients.ShouldBe(1);

        using var context = CreateContext();
        var membershipAfter = await context.Set<Membership>().FirstAsync(m => m.ClientId == leaverClientId);
        membershipAfter.ValidUntil.ShouldNotBeNull();

        var syncLogAfter = await context.Set<IdentityProviderSyncLog>().FirstAsync(l => l.ClientId == leaverClientId);
        syncLogAfter.IsActiveInSource.ShouldBeFalse();
    }

    [Test]
    public async Task SyncClientsAsync_IncompleteDirectoryResult_AbortsAndLeavesMembershipsAlone()
    {
        var providerId = await CreateTestProviderAsync();
        var externalId = $"{TestDataPrefix}guid-{Guid.NewGuid():N}";
        var clientId = await SeedActiveClientAsync(providerId, externalId, "Employee");

        StubDirectory(providerId, isComplete: false);

        using var scope = _factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new SyncClientsCommand(providerId));

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldBe(ClientSyncService.IncompleteResultMessage);
        await AssertEmployeeUntouchedAsync(providerId, clientId);
    }

    [Test]
    public async Task SyncClientsAsync_EmptyDirectoryResult_AbortsAndLeavesMembershipsAlone()
    {
        var providerId = await CreateTestProviderAsync();
        var externalId = $"{TestDataPrefix}guid-{Guid.NewGuid():N}";
        var clientId = await SeedActiveClientAsync(providerId, externalId, "Employee");

        StubDirectory(providerId, isComplete: true);

        using var scope = _factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new SyncClientsCommand(providerId));

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldBe(ClientSyncService.EmptyResultMessage);
        await AssertEmployeeUntouchedAsync(providerId, clientId);
    }

    private void StubDirectory(Guid providerId, bool isComplete, params LdapUserEntry[] entries)
    {
        _factory.FakeLdapService.GetUsersAsync(Arg.Is<IdentityProvider>(p => p.Id == providerId))
            .Returns(new LdapUserSearchResult(entries, isComplete));
    }

    private async Task<Guid?> FindClientIdByExternalIdAsync(string externalId)
    {
        using var context = CreateContext();
        var client = await context.Client.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.LdapExternalId == externalId);
        if (client != null)
        {
            _createdClientIds.Add(client.Id);
        }

        return client?.Id;
    }

    private async Task<Guid> SeedActiveClientAsync(Guid providerId, string externalId, string nameSuffix)
    {
        using var seedContext = CreateContext();
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Name = $"{TestDataPrefix}{nameSuffix}",
            FirstName = "IntegrationTest",
            Type = EntityTypeEnum.Employee,
            Gender = GenderEnum.Intersexuality,
            IdentityProviderId = providerId,
            LdapExternalId = externalId,
        };
        seedContext.Add(client);

        seedContext.Add(new Membership
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            ValidFrom = DateTime.UtcNow.AddYears(-1),
            ValidUntil = null,
        });

        seedContext.Add(new IdentityProviderSyncLog
        {
            Id = Guid.NewGuid(),
            IdentityProviderId = providerId,
            ClientId = client.Id,
            ExternalId = externalId,
            ExternalDn = $"cn={TestDataPrefix}{nameSuffix},dc=example,dc=com",
            LastSyncTime = DateTime.UtcNow.AddDays(-1),
            IsActiveInSource = true,
        });

        await seedContext.SaveChangesAsync();
        _createdClientIds.Add(client.Id);
        return client.Id;
    }

    private async Task AssertEmployeeUntouchedAsync(Guid providerId, Guid clientId)
    {
        using var context = CreateContext();
        var membership = await context.Set<Membership>().FirstAsync(m => m.ClientId == clientId);
        membership.ValidUntil.ShouldBeNull();

        var syncLog = await context.Set<IdentityProviderSyncLog>().FirstAsync(l => l.ClientId == clientId);
        syncLog.IsActiveInSource.ShouldBeTrue();

        var provider = await context.Set<IdentityProvider>().FirstAsync(p => p.Id == providerId);
        provider.LastSyncError.ShouldNotBeNullOrEmpty();
    }
}
