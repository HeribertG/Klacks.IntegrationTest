// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// A/B effectiveness tests for the admin-editable USP settings (mechanism A) against the REAL
/// PostgreSQL database: each test writes a setting value X inside a database transaction, calls the
/// production resolver on the SAME DbContext, asserts the resolved result, then writes value Y and
/// asserts a DIFFERENT result. The transaction is ALWAYS rolled back in TearDown (also on failure),
/// so no global setting value ever leaks into the shared dev database on port 5434.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Associations;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Settings;

[TestFixture]
[Category("RealDatabase")]
public class UspSettingsEffectivenessTests
{
    private const string EmptyValue = "";
    private const string BlockValue = "block";
    private const string WarnValue = "warn";

    private DataBaseContext _context = null!;
    private IDbContextTransaction _transaction = null!;

    [SetUp]
    public async Task SetUp()
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _transaction = await _context.Database.BeginTransactionAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_transaction is not null)
        {
            await _transaction.RollbackAsync();
            await _transaction.DisposeAsync();
        }

        _context?.Dispose();
    }

    [Test]
    public async Task OvertimeConfigResolver_TierLadderSettings_DriveResolvedLadder()
    {
        var resolver = new OvertimeConfigResolver(_context, new ClientContractDataProvider(_context, NullLogger<ClientContractDataProvider>.Instance));
        var clientWithoutContract = Guid.NewGuid();
        var date = new DateOnly(2091, 3, 10);

        await UpsertSettingAsync(SettingKeys.OvertimeBasis, "day");
        await UpsertSettingAsync(SettingKeys.OvertimeRateMode, "multiplier");
        await UpsertSettingAsync(SettingKeys.OvertimeTier1AfterHours, "9");
        await UpsertSettingAsync(SettingKeys.OvertimeTier1Rate, "1.25");
        await UpsertSettingAsync(SettingKeys.OvertimeTier2AfterHours, "11");
        await UpsertSettingAsync(SettingKeys.OvertimeTier2Rate, "1.5");
        await UpsertSettingAsync(SettingKeys.OvertimeTier3AfterHours, EmptyValue);
        await UpsertSettingAsync(SettingKeys.OvertimeTier3Rate, EmptyValue);

        var configA = await resolver.ResolveAsync(clientWithoutContract, date);

        configA.Basis.ShouldBe(OvertimeBasis.Day);
        configA.RateMode.ShouldBe(SurchargeRateMode.Multiplier);
        configA.Tiers.Count.ShouldBe(2);
        configA.Tiers[0].AfterHours.ShouldBe(9m);
        configA.Tiers[0].Rate.ShouldBe(1.25m);
        configA.Tiers[1].AfterHours.ShouldBe(11m);
        configA.Tiers[1].Rate.ShouldBe(1.5m);

        await UpsertSettingAsync(SettingKeys.OvertimeBasis, "week");
        await UpsertSettingAsync(SettingKeys.OvertimeRateMode, "fixedperhour");
        await UpsertSettingAsync(SettingKeys.OvertimeTier1AfterHours, "45");
        await UpsertSettingAsync(SettingKeys.OvertimeTier1Rate, "6");
        await UpsertSettingAsync(SettingKeys.OvertimeTier2AfterHours, EmptyValue);
        await UpsertSettingAsync(SettingKeys.OvertimeTier2Rate, EmptyValue);

        var configB = await resolver.ResolveAsync(clientWithoutContract, date);

        configB.Basis.ShouldBe(OvertimeBasis.Week, "changing OVERTIME_BASIS must change the resolved basis");
        configB.RateMode.ShouldBe(SurchargeRateMode.FixedPerHour, "changing OVERTIME_RATE_MODE must change the resolved rate mode");
        configB.Tiers.Count.ShouldBe(1, "clearing tier 2/3 must shrink the resolved ladder");
        configB.Tiers[0].AfterHours.ShouldBe(45m);
        configB.Tiers[0].Rate.ShouldBe(6m);
    }

    [Test]
    public async Task ComplianceEnforcementResolver_PerRuleKey_DrivesMode()
    {
        var resolver = new ComplianceEnforcementResolver(new TransactionalSettingsReader(_context), NullLogger<ComplianceEnforcementResolver>.Instance);

        await UpsertSettingAsync(SettingKeys.ComplianceEnforcementMaxWeeklyHours, BlockValue);
        (await resolver.GetModeAsync(ComplianceRuleNames.MaxWeeklyHours))
            .ShouldBe(RuleEnforcementMode.Block, "per-rule key 'block' must yield Block");

        await UpsertSettingAsync(SettingKeys.ComplianceEnforcementMaxWeeklyHours, WarnValue);
        (await resolver.GetModeAsync(ComplianceRuleNames.MaxWeeklyHours))
            .ShouldBe(RuleEnforcementMode.Warn, "per-rule key 'warn' must yield Warn");
    }

    [Test]
    public async Task ComplianceEnforcementResolver_DefaultModeFallback_AppliesWhenPerRuleKeyIsEmpty()
    {
        var resolver = new ComplianceEnforcementResolver(new TransactionalSettingsReader(_context), NullLogger<ComplianceEnforcementResolver>.Instance);

        await UpsertSettingAsync(SettingKeys.ComplianceEnforcementMinRestHours, EmptyValue);
        await UpsertSettingAsync(SettingKeys.ComplianceEnforcementDefaultMode, BlockValue);
        (await resolver.GetModeAsync(ComplianceRuleNames.MinRestHours))
            .ShouldBe(RuleEnforcementMode.Block, "empty per-rule key must fall back to DEFAULT_MODE=block");

        await UpsertSettingAsync(SettingKeys.ComplianceEnforcementDefaultMode, EmptyValue);
        (await resolver.GetModeAsync(ComplianceRuleNames.MinRestHours))
            .ShouldBe(RuleEnforcementMode.Warn, "with neither key configured the resolver must default to Warn");

        await UpsertSettingAsync(SettingKeys.ComplianceEnforcementMinRestHours, BlockValue);
        await UpsertSettingAsync(SettingKeys.ComplianceEnforcementDefaultMode, WarnValue);
        (await resolver.GetModeAsync(ComplianceRuleNames.MinRestHours))
            .ShouldBe(RuleEnforcementMode.Block, "the per-rule key must win over DEFAULT_MODE");
    }

    [Test]
    public async Task ClientContractDataProvider_NightWindowSettings_DriveContractlessDefaults()
    {
        var clientWithoutContract = Guid.NewGuid();
        var date = new DateOnly(2091, 3, 10);

        await UpsertSettingAsync(SettingKeys.SurchargeNightStart, "22:00");
        await UpsertSettingAsync(SettingKeys.SurchargeNightEnd, "05:00");

        // A fresh provider instance per resolution, not one shared across both settings edits: the
        // provider deliberately memoizes default settings for the lifetime of one DI scope
        // (ClientContractDataProvider.cs, LoadDefaultSettingsAsync) because in production a settings
        // write and a contract-data resolve never share a scope - the settings handler only queues a
        // recalculation, which runs in its own fresh scope. Reusing one provider instance across both
        // edits here would exercise a scope shape that never occurs in production and would just be
        // testing the memoization cache instead of the resolver.
        var providerA = new ClientContractDataProvider(_context, NullLogger<ClientContractDataProvider>.Instance);
        var dataA = await providerA.GetEffectiveContractDataAsync(clientWithoutContract, date);

        dataA.NightStart.ShouldBe("22:00");
        dataA.NightEnd.ShouldBe("05:00");

        await UpsertSettingAsync(SettingKeys.SurchargeNightStart, "23:15");
        await UpsertSettingAsync(SettingKeys.SurchargeNightEnd, "06:45");

        var providerB = new ClientContractDataProvider(_context, NullLogger<ClientContractDataProvider>.Instance);
        var dataB = await providerB.GetEffectiveContractDataAsync(clientWithoutContract, date);

        dataB.NightStart.ShouldBe("23:15", "editing SURCHARGE_NIGHT_START must move the effective night window start");
        dataB.NightEnd.ShouldBe("06:45", "editing SURCHARGE_NIGHT_END must move the effective night window end");
    }

    private async Task UpsertSettingAsync(string type, string value)
    {
        var existing = await _context.Settings.FirstOrDefaultAsync(s => s.Type == type);
        if (existing is null)
        {
            _context.Settings.Add(new Klacks.Api.Domain.Models.Settings.Settings
            {
                Id = Guid.NewGuid(),
                Type = type,
                Value = value,
            });
        }
        else
        {
            existing.Value = value;
        }

        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// Minimal ISettingsReader over the SAME transactional DbContext, so a resolver under test reads
    /// the uncommitted setting values written by the test (the production SettingsRepository would
    /// require its own unrelated service dependencies).
    /// </summary>
    private sealed class TransactionalSettingsReader : ISettingsReader
    {
        private readonly DataBaseContext _context;

        public TransactionalSettingsReader(DataBaseContext context)
        {
            _context = context;
        }

        public Task<Klacks.Api.Domain.Models.Settings.Settings?> GetSetting(string type)
        {
            return _context.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Type == type);
        }

        public async Task<IReadOnlyDictionary<string, string>> GetSettingsByTypesAsync(IEnumerable<string> types)
        {
            var typeList = types.ToList();
            return await _context.Settings
                .AsNoTracking()
                .Where(s => typeList.Contains(s.Type))
                .ToDictionaryAsync(s => s.Type, s => s.Value);
        }
    }
}
