// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The shift preference dialog sends back the list it loaded, so the read and the bulk save must agree, against PostgreSQL
/// with the real repository, shift visibility guard and visibility query. Tests: a list loaded while a scenario holds a clone
/// of a preferred shift saves, and the preference clones of that scenario stay; a real row an older save left on a clone is
/// neither listed nor refused and goes with the next save; a stored preference on a shift the caller may not see stays
/// saveable while a new one is refused; a missing, a hidden and a tokened shift get the same 404 from the bulk save and from
/// the single set, also for an unrestricted caller. Every row carries the INTEGRATION_TEST_PREFROUNDTRIP_ prefix and the
/// cleanup deletes exactly those.
/// </summary>

using Klacks.Api.Application.Commands.ClientShiftPreferences;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Handlers.ClientShiftPreferences;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.ClientShiftPreferences;
using Klacks.Api.Application.Services.Shifts;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.ShiftSchedule;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Associations;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.AnalyseScenarios;

[TestFixture]
[Category("RealDatabase")]
public class ClientShiftPreferenceScenarioRoundTripTests
{
    private const string TestPrefix = "INTEGRATION_TEST_PREFROUNDTRIP_";

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private ClientShiftPreferenceRepository _repository = null!;
    private ClientShiftPreferenceMapper _mapper = null!;
    private IClientVisibilityGuard _clientGuard = null!;
    private IShiftGroupFilterService _groupFilter = null!;
    private ShiftVisibilityGuard _shiftGuard = null!;
    private IUnitOfWork _unitOfWork = null!;
    private Group _visibleGroup = null!;
    private Group _hiddenGroup = null!;
    private Client _client = null!;
    private Shift _preferred = null!;
    private Shift _clone = null!;
    private Shift _hidden = null!;
    private Guid _scenarioToken;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
        await using var context = NewContext();
        await CleanupAsync(context);
    }

    [SetUp]
    public async Task SetUp()
    {
        _context = NewContext();
        _repository = new ClientShiftPreferenceRepository(_context, Substitute.For<ILogger<ClientShiftPreference>>());
        _mapper = new ClientShiftPreferenceMapper();
        _clientGuard = Substitute.For<IClientVisibilityGuard>();
        _clientGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _groupFilter = Substitute.For<IShiftGroupFilterService>();
        _groupFilter.ResolveShiftListGroupIdsAsync(null).Returns(Task.FromResult<List<Guid>?>(null));
        _shiftGuard = new ShiftVisibilityGuard(
            _groupFilter,
            new ShiftVisibilityReadRepository(_context),
            Substitute.For<IGroupVisibilityGuard>(),
            Substitute.For<ILogger<ShiftVisibilityGuard>>());
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.CompleteAsync().Returns(_ => _context.SaveChangesAsync());
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<List<ClientShiftPreferenceResource>>>>())
            .Returns(call => call.ArgAt<Func<Task<List<ClientShiftPreferenceResource>>>>(0)());

        await SeedAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync(_context);
        await _context.DisposeAsync();
    }

    [Test]
    public async Task ALoadedListSaves_WhileAScenarioHoldsACloneOfThePreferredShift_AndTheClonesStay()
    {
        var real = await AddPreferenceAsync(_preferred.Id, null);
        var clone = await AddPreferenceAsync(_clone.Id, _scenarioToken);

        var loaded = await ListAsync();
        loaded.Select(preference => preference.ShiftId).ShouldBe(new[] { _preferred.Id });

        var saved = await SaveAsync(loaded);

        saved.Select(preference => preference.ShiftId).ShouldBe(new[] { _preferred.Id });
        _context.ChangeTracker.Clear();
        var rows = await _context.ClientShiftPreference.AsNoTracking().Where(row => row.ClientId == _client.Id).ToListAsync();
        rows.Count.ShouldBe(2);
        rows.ShouldContain(row => row.Id == clone.Id && row.ShiftId == _clone.Id && row.AnalyseToken == _scenarioToken);
        rows.ShouldContain(row => row.Id != real.Id && row.ShiftId == _preferred.Id && row.AnalyseToken == null);
    }

    [Test]
    public async Task ARealRowLeftOnAClone_IsNotListed_AndGoesWithTheNextSave()
    {
        await AddPreferenceAsync(_preferred.Id, null);
        await AddPreferenceAsync(_clone.Id, null);

        var loaded = await ListAsync();
        loaded.Select(preference => preference.ShiftId).ShouldBe(new[] { _preferred.Id });

        await SaveAsync(loaded);

        _context.ChangeTracker.Clear();
        var rows = await _context.ClientShiftPreference.AsNoTracking().Where(row => row.ClientId == _client.Id).ToListAsync();
        rows.Select(row => row.ShiftId).ShouldBe(new[] { _preferred.Id });
    }

    [Test]
    public async Task ARestrictedCaller_KeepsAStoredPreferenceOnAHiddenShift_ButCannotAddOne()
    {
        await AddPreferenceAsync(_hidden.Id, null);
        Restrict();

        var loaded = await ListAsync();
        loaded.Select(preference => preference.ShiftId).ShouldBe(new[] { _hidden.Id });

        await Should.NotThrowAsync(() => SaveAsync(loaded));

        _context.ChangeTracker.Clear();
        (await _context.ClientShiftPreference.AsNoTracking().CountAsync(row => row.ClientId == _client.Id && row.ShiftId == _hidden.Id))
            .ShouldBe(1);

        var other = Guid.NewGuid();
        await AddPreferenceOnANewClientAsync(other);
        await AssertRefusedAsync(other, _hidden.Id);
    }

    [Test]
    public async Task AMissingAHiddenAndATokenedShift_GetTheSameAnswer_FromBothWrites()
    {
        var missing = Guid.NewGuid();

        await AssertRefusedAsync(_client.Id, missing);
        await AssertRefusedAsync(_client.Id, _clone.Id);

        Restrict();
        await AssertRefusedAsync(_client.Id, _hidden.Id);
        await AssertRefusedAsync(_client.Id, missing);
    }

    private async Task AssertRefusedAsync(Guid clientId, Guid shiftId)
    {
        var expected = string.Format(ShiftVisibilityMessages.ShiftNotFound, shiftId);

        var bulk = await Should.ThrowAsync<KeyNotFoundException>(() => SaveHandler().Handle(
            new SaveClientShiftPreferencesCommand(
                clientId, [new ClientShiftPreferenceResource { ShiftId = shiftId, PreferenceType = ShiftPreferenceType.Preferred }]),
            CancellationToken.None));
        var single = await Should.ThrowAsync<KeyNotFoundException>(() => SetHandler().Handle(
            new SetShiftPreferenceCommand(clientId, shiftId, ShiftPreferenceType.Preferred), CancellationToken.None));

        bulk.Message.ShouldBe(expected);
        single.Message.ShouldBe(expected);
    }

    private void Restrict() =>
        _groupFilter.ResolveShiftListGroupIdsAsync(null).Returns(Task.FromResult<List<Guid>?>([_visibleGroup.Id]));

    private async Task<List<ClientShiftPreferenceResource>> ListAsync()
    {
        _context.ChangeTracker.Clear();
        var handler = new ListByClientQueryHandler(
            _repository, _clientGuard, _mapper, Substitute.For<ILogger<ListByClientQueryHandler>>());
        return await handler.Handle(new ListByClientQuery(_client.Id), CancellationToken.None);
    }

    private Task<List<ClientShiftPreferenceResource>> SaveAsync(List<ClientShiftPreferenceResource> list) =>
        SaveHandler().Handle(new SaveClientShiftPreferencesCommand(_client.Id, list), CancellationToken.None);

    private SaveClientShiftPreferencesCommandHandler SaveHandler() =>
        new(_repository, _clientGuard, _shiftGuard, _mapper, _unitOfWork,
            Substitute.For<ILogger<SaveClientShiftPreferencesCommandHandler>>());

    private SetShiftPreferenceCommandHandler SetHandler() => new(_repository, _clientGuard, _shiftGuard, _unitOfWork);

    private async Task<ClientShiftPreference> AddPreferenceAsync(Guid shiftId, Guid? token)
    {
        var row = new ClientShiftPreference
        {
            Id = Guid.NewGuid(),
            ClientId = _client.Id,
            ShiftId = shiftId,
            PreferenceType = ShiftPreferenceType.Preferred,
            AnalyseToken = token
        };
        await _context.ClientShiftPreference.AddAsync(row);
        await _context.SaveChangesAsync();
        return row;
    }

    private async Task AddPreferenceOnANewClientAsync(Guid clientId)
    {
        await _context.Set<Client>().AddAsync(new Client
        {
            Id = clientId,
            Name = TestPrefix + "OTHER",
            FirstName = "Test",
            Company = string.Empty,
            LegalEntity = false
        });
        await _context.SaveChangesAsync();
    }

    private async Task SeedAsync()
    {
        _scenarioToken = Guid.NewGuid();
        _visibleGroup = NewGroup("VISIBLE");
        _hiddenGroup = NewGroup("HIDDEN");
        _client = new Client
        {
            Id = Guid.NewGuid(),
            Name = TestPrefix + "CLIENT",
            FirstName = "Test",
            Company = string.Empty,
            LegalEntity = false
        };
        await _context.Set<Group>().AddRangeAsync(_visibleGroup, _hiddenGroup);
        await _context.Set<Client>().AddAsync(_client);
        await _context.SaveChangesAsync();

        _preferred = NewShift("PREFERRED", _visibleGroup.Id, null, null);
        _hidden = NewShift("HIDDEN", _hiddenGroup.Id, null, null);
        await _context.Shift.AddRangeAsync(_preferred, _hidden);
        await _context.SaveChangesAsync();

        _clone = NewShift("CLONE", _visibleGroup.Id, _scenarioToken, _preferred.Id);
        await _context.Shift.AddAsync(_clone);
        await _context.SaveChangesAsync();
    }

    private static Group NewGroup(string suffix) => new()
    {
        Id = Guid.NewGuid(),
        Name = TestPrefix + suffix,
        Description = string.Empty,
        ValidFrom = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private static Shift NewShift(string suffix, Guid groupId, Guid? token, Guid? sourceId)
    {
        var id = Guid.NewGuid();
        return new Shift
        {
            Id = id,
            Name = TestPrefix + suffix,
            Abbreviation = "TST",
            Description = "Preference round trip",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = new DateOnly(2099, 1, 1),
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            WorkTime = 8m,
            IsMonday = true,
            Quantity = 1,
            SumEmployees = 1,
            AnalyseToken = token,
            ScenarioSourceShiftId = sourceId,
            GroupItems = [new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ShiftId = id, AnalyseToken = token }]
        };
    }

    private DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private static async Task CleanupAsync(DataBaseContext context)
    {
        var sql = $@"
            DELETE FROM client_shift_preference WHERE client_id IN (SELECT id FROM client WHERE name LIKE '{TestPrefix}%');
            DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{TestPrefix}%')
                OR group_id IN (SELECT id FROM ""group"" WHERE name LIKE '{TestPrefix}%');
            UPDATE shift SET scenario_source_shift_id = NULL WHERE name LIKE '{TestPrefix}%';
            DELETE FROM shift WHERE name LIKE '{TestPrefix}%';
            DELETE FROM ""group"" WHERE name LIKE '{TestPrefix}%';
            DELETE FROM client WHERE name LIKE '{TestPrefix}%';
        ";
        await context.Database.ExecuteSqlRawAsync(sql);
    }
}
