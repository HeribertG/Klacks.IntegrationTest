// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Shouldly;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Services.Settings;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.Settings;

[TestFixture]
[Category("RealDatabase")]
public class IdentityProviderSecretEncryptionTests
{
    private const string TestNamePrefix = "INTEGRATION_TEST_ENCRYPTION_";
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
    }

    [TearDown]
    public async Task TearDown()
    {
        // Plain ADO.NET on purpose: constructing a DataBaseContext without a real
        // ISettingsEncryptionService here would let EF Core's process-wide model cache
        // (keyed only by DbContext type, not by constructor args) permanently freeze the
        // IdentityProvider model WITHOUT the encryption HasConversion for the rest of this
        // test process - silently disabling encryption for every later test/context.
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM identity_providers WHERE name LIKE @prefix";
        command.Parameters.AddWithValue("prefix", TestNamePrefix + "%");
        await command.ExecuteNonQueryAsync();
    }

    private DataBaseContext CreateContextWithRealEncryption()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        return new DataBaseContext(options, mockHttpContextAccessor, CreateRealEncryptionService());
    }

    private static ISettingsEncryptionService CreateRealEncryptionService()
    {
        var dataProtectionProvider = DataProtectionProvider.Create("Klacks.IntegrationTest.IdentityProviderEncryption");
        var logger = Substitute.For<ILogger<SettingsEncryptionService>>();
        return new SettingsEncryptionService(dataProtectionProvider, logger);
    }

    [Test]
    public async Task Insert_WithRealEncryptionService_StoresCiphertextAtRest()
    {
        var name = $"{TestNamePrefix}INSERT_{Guid.NewGuid():N}";
        const string plaintextBindPassword = "super-secret-bind-password";
        const string plaintextClientSecret = "super-secret-client-secret";

        // Act: insert through the same kind of DI-shaped context (real, non-null encryption
        // service) that AddDbContextPool hands out in the running application.
        await using (var writeContext = CreateContextWithRealEncryption())
        {
            writeContext.IdentityProviders.Add(new IdentityProvider
            {
                Id = Guid.NewGuid(),
                Name = name,
                Type = IdentityProviderType.Ldap,
                BindPassword = plaintextBindPassword,
                ClientSecret = plaintextClientSecret,
            });
            await writeContext.SaveChangesAsync();
        }

        // Assert: the raw DB column is ciphertext, never plaintext. Read via plain ADO.NET,
        // not EF, so the assertion cannot be fooled by the entity's own decrypting converter.
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT bind_password, client_secret FROM identity_providers WHERE name = @name";
        command.Parameters.AddWithValue("name", name);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        var rawBindPassword = reader.GetString(0);
        var rawClientSecret = reader.GetString(1);
        await reader.CloseAsync();

        rawBindPassword.ShouldStartWith("ENC:");
        rawBindPassword.ShouldNotBe(plaintextBindPassword);
        rawClientSecret.ShouldStartWith("ENC:");
        rawClientSecret.ShouldNotBe(plaintextClientSecret);

        // Assert: reading back through the real encryption service round-trips to plaintext.
        await using var readContext = CreateContextWithRealEncryption();
        var entity = await readContext.IdentityProviders.SingleAsync(p => p.Name == name);
        entity.BindPassword.ShouldBe(plaintextBindPassword);
        entity.ClientSecret.ShouldBe(plaintextClientSecret);
    }
}
