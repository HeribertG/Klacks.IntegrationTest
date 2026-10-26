// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.DTOs.Geocoding;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Geocoding;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Geocoding;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Geocoding;
using Klacks.Api.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Staffs;

/// <summary>
/// Real-Postgres tests of the address geocoding persistence (the EF InMemory provider runs neither ExecuteUpdate
/// nor the INSERT ... ON CONFLICT upserts): the due query (customers always, employees only when allowed, never
/// attempted, attempted for another postcode, retry windows, requested first, a single coordinate never a
/// candidate), the conditional fill that never overwrites coordinates and loses against an edited postcode, the
/// state only filled when empty, the audit row, requests and attempts racing on one row, and the locality cache
/// upsert keeping the newer answer.
/// Connection: TestHostDatabase (localhost:5434, database klacks). Fixture rows carry the INTEGRATION_TEST_ prefix.
/// </summary>
[TestFixture]
[Category("RealDatabase")]
public class AddressGeocodingRepositoryPostgresTests
{
    private const string Prefix = "INTEGRATION_TEST_";
    private const string TestCountry = "ZZ";
    private const int Unlimited = 100000;
    private static readonly DateTime Now = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

    private DataBaseContext _context = null!;
    private AddressGeocodingStateRepository _repository = null!;
    private GeocodedLocalityRepository _localities = null!;
    private readonly HashSet<Guid> _ownIds = [];

    [SetUp]
    public async Task SetUp()
    {
        _context = NewContext();
        _repository = new AddressGeocodingStateRepository(_context);
        _localities = new GeocodedLocalityRepository(_context);
        await CleanupAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupAsync();
        await _context.DisposeAsync();
    }

    [Test]
    public async Task GetDueCandidates_AppliesPrivacyCompletenessAndCoordinates()
    {
        var customer = await AddAddressAsync(EntityTypeEnum.Customer);
        var employee = await AddAddressAsync(EntityTypeEnum.Employee);
        var external = await AddAddressAsync(EntityTypeEnum.ExternEmp);
        var withoutZip = await AddAddressAsync(EntityTypeEnum.Customer, zip: " ");
        var geocoded = await AddAddressAsync(EntityTypeEnum.Customer, latitude: 47.1, longitude: 8.1);
        var latitudeOnly = await AddAddressAsync(EntityTypeEnum.Customer, latitude: 47.1);
        var deleted = await AddAddressAsync(EntityTypeEnum.Customer, deleted: true);

        var customersOnly = await DueIdsAsync(includeEmployees: false);
        var everyone = await DueIdsAsync(includeEmployees: true);

        customersOnly.ShouldBe([customer.Id], ignoreOrder: true);
        everyone.ShouldBe([customer.Id, employee.Id, external.Id], ignoreOrder: true);
        everyone.ShouldNotContain(withoutZip.Id);
        everyone.ShouldNotContain(geocoded.Id);
        everyone.ShouldNotContain(latitudeOnly.Id);
        everyone.ShouldNotContain(deleted.Id);
    }

    [Test]
    public async Task GetDueCandidates_HonoursAttemptsRetryWindowsAndChangedLocality()
    {
        var fresh = await AddAddressAsync(EntityTypeEnum.Customer);
        var recentNotFound = await AddAddressAsync(EntityTypeEnum.Customer);
        var oldNotFound = await AddAddressAsync(EntityTypeEnum.Customer);
        var recentUnavailable = await AddAddressAsync(EntityTypeEnum.Customer);
        var oldUnavailable = await AddAddressAsync(EntityTypeEnum.Customer);
        var changedZip = await AddAddressAsync(EntityTypeEnum.Customer);

        await _repository.RecordAttemptAsync(Candidate(recentNotFound), AddressGeocodingOutcome.NotFound, Now.AddDays(-1));
        await _repository.RecordAttemptAsync(Candidate(oldNotFound), AddressGeocodingOutcome.NotFound, Now.AddDays(-40));
        await _repository.RecordAttemptAsync(Candidate(recentUnavailable), AddressGeocodingOutcome.Unavailable, Now.AddMinutes(-5));
        await _repository.RecordAttemptAsync(Candidate(oldUnavailable), AddressGeocodingOutcome.Unavailable, Now.AddHours(-2));
        await _repository.RecordAttemptAsync(Candidate(changedZip), AddressGeocodingOutcome.NotFound, Now.AddDays(-1));
        await _context.Database.ExecuteSqlInterpolatedAsync($"UPDATE address SET zip = '9999' WHERE id = {changedZip.Id}");

        var due = await DueIdsAsync(includeEmployees: false);

        due.ShouldBe([fresh.Id, oldNotFound.Id, oldUnavailable.Id, changedZip.Id], ignoreOrder: true);
    }

    [Test]
    public async Task GetDueCandidates_RequestedAddressesComeFirstAndCarryTheirAttribution()
    {
        var plain = await AddAddressAsync(EntityTypeEnum.Customer);
        var requested = await AddAddressAsync(EntityTypeEnum.Customer);
        await _repository.RecordAttemptAsync(Candidate(requested), AddressGeocodingOutcome.NotFound, Now.AddDays(-1));
        var userId = Guid.NewGuid();

        var marked = await _repository.RequestAsync(
            [requested.Id, requested.Id], new AddressGeocodingAttribution(AddressGeocodingTrigger.Assistant, userId), Now);
        var due = await DueAsync(includeEmployees: false);

        marked.ShouldBe(1);
        var mine = due.Where(c => c.AddressId == plain.Id || c.AddressId == requested.Id).ToList();
        mine.Select(c => c.AddressId).ShouldBe([requested.Id, plain.Id]);
        mine[0].PendingRequest.ShouldBe(new AddressGeocodingAttribution(AddressGeocodingTrigger.Assistant, userId));
        mine[1].PendingRequest.ShouldBeNull();
        due.First().PendingRequest.ShouldNotBeNull();
    }

    [Test]
    public async Task TryFill_WritesCoordinatesStateAndAudit_ClearsTheRequest()
    {
        var address = await AddAddressAsync(EntityTypeEnum.Customer);
        var userId = Guid.NewGuid();
        await _repository.RequestAsync([address.Id], new AddressGeocodingAttribution(AddressGeocodingTrigger.Assistant, userId), Now);

        var filled = await _repository.TryFillAsync(Fill(address, stateIfEmpty: "ZH", userId: userId));

        filled.ShouldBeTrue();
        var stored = await ReloadAsync(address.Id);
        stored.Latitude.ShouldBe(47.37);
        stored.Longitude.ShouldBe(8.54);
        stored.State.ShouldBe("ZH");
        var state = await _context.AddressGeocodingStates.AsNoTracking().SingleAsync(s => s.AddressId == address.Id);
        state.Outcome.ShouldBe(AddressGeocodingOutcome.Filled);
        state.FillSource.ShouldBe(AddressGeocodingSource.NominatimLocality);
        state.FilledBy.ShouldBe(AddressGeocodingTrigger.Assistant);
        state.FilledByUserId.ShouldBe(userId);
        state.FilledAt!.Value.ToUniversalTime().ShouldBe(Now);
        state.FilledLatitude.ShouldBe(47.37);
        state.AttemptedZip.ShouldBe(address.Zip);
        state.RequestedAt.ShouldBeNull();
        state.RequestedBy.ShouldBeNull();
    }

    [Test]
    public async Task TryFill_NeverOverwritesCoordinatesOrState()
    {
        var geocoded = await AddAddressAsync(EntityTypeEnum.Customer, latitude: 46.0, longitude: 7.0);
        var withState = await AddAddressAsync(EntityTypeEnum.Customer, state: "BE");

        var overwrote = await _repository.TryFillAsync(Fill(geocoded, stateIfEmpty: "ZH"));
        var filledWithState = await _repository.TryFillAsync(Fill(withState, stateIfEmpty: "ZH"));

        overwrote.ShouldBeFalse();
        (await ReloadAsync(geocoded.Id)).Latitude.ShouldBe(46.0);
        (await _context.AddressGeocodingStates.AsNoTracking().AnyAsync(s => s.AddressId == geocoded.Id)).ShouldBeFalse();
        filledWithState.ShouldBeTrue();
        (await ReloadAsync(withState.Id)).State.ShouldBe("BE");
    }

    [Test]
    public async Task TryFill_LosesAgainstAnEditedPostcode()
    {
        var address = await AddAddressAsync(EntityTypeEnum.Customer);
        var fill = Fill(address, stateIfEmpty: null);
        await _context.Database.ExecuteSqlInterpolatedAsync($"UPDATE address SET zip = '9999' WHERE id = {address.Id}");

        var filled = await _repository.TryFillAsync(fill);

        filled.ShouldBeFalse();
        (await ReloadAsync(address.Id)).Latitude.ShouldBeNull();
    }

    [Test]
    public async Task GetDueCandidates_FilledAddressWhoseCoordinatesWereLost_IsDueAgain()
    {
        var address = await AddAddressAsync(EntityTypeEnum.Customer);
        (await _repository.TryFillAsync(Fill(address, stateIfEmpty: null))).ShouldBeTrue();
        (await DueIdsAsync(includeEmployees: false)).ShouldNotContain(address.Id);

        await _context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE address SET latitude = NULL, longitude = NULL WHERE id = {address.Id}");

        (await DueIdsAsync(includeEmployees: false)).ShouldContain(address.Id);
    }

    [Test]
    public async Task AutofillCoordinateWriter_FillsOnlyEmptyCoordinatesAndRecordsTheStreetSource()
    {
        var empty = await AddAddressAsync(EntityTypeEnum.Customer);
        var geocoded = await AddAddressAsync(EntityTypeEnum.Customer, latitude: 46.0, longitude: 7.0);
        var writer = new AddressCoordinateWriter(
            _context, _repository, TimeProvider.System, NullLogger<AddressCoordinateWriter>.Instance);

        await writer.UpdateCoordinatesAsync(empty.Id, 47.1, 8.1);
        await writer.UpdateCoordinatesAsync(geocoded.Id, 47.1, 8.1);

        (await ReloadAsync(empty.Id)).Latitude.ShouldBe(47.1);
        (await ReloadAsync(geocoded.Id)).Latitude.ShouldBe(46.0);
        var state = await _context.AddressGeocodingStates.AsNoTracking().SingleAsync(s => s.AddressId == empty.Id);
        state.FillSource.ShouldBe(AddressGeocodingSource.NominatimStreet);
        state.FilledBy.ShouldBe(AddressGeocodingTrigger.Autofill);
    }

    [Test]
    public async Task GetMissingCoordinates_ByClientAndById_ReturnsOnlyAddressesWithoutCoordinates()
    {
        var missing = await AddAddressAsync(EntityTypeEnum.Employee, zip: string.Empty);
        var geocoded = await AddAddressAsync(EntityTypeEnum.Customer, latitude: 47.0, longitude: 8.0);

        var byClient = await _repository.GetMissingCoordinatesAsync([missing.ClientId, geocoded.ClientId]);
        var byId = await _repository.GetMissingCoordinatesByIdsAsync([missing.Id, geocoded.Id]);

        byClient.Select(c => c.AddressId).ShouldBe([missing.Id]);
        byId.Select(c => c.AddressId).ShouldBe([missing.Id]);
        byClient[0].ClientType.ShouldBe(EntityTypeEnum.Employee);
        byClient[0].ClientName.ShouldContain(Prefix);
    }

    [Test]
    public async Task LocalityCache_UpsertKeepsTheNewerAnswer()
    {
        var key = GeocodingLocalityKey.Create(TestCountry, Prefix + "8001", "Testort")!;

        await _localities.StoreAsync(Locality(key, found: false, Now.AddDays(-40)));
        await _localities.StoreAsync(Locality(key, found: true, Now));
        await _localities.StoreAsync(Locality(key, found: false, Now.AddDays(-1)));

        var stored = await _localities.GetAsync(key);
        stored.ShouldNotBeNull();
        stored.Found.ShouldBeTrue();
        stored.Latitude.ShouldBe(47.37);
        stored.State.ShouldBe("Zürich");
        stored.ResolvedAt.ToUniversalTime().ShouldBe(Now);
    }

    private async Task<List<Guid>> DueIdsAsync(bool includeEmployees) =>
        (await DueAsync(includeEmployees)).Select(c => c.AddressId).Where(_ownIds.Contains).ToList();

    private Task<List<AddressGeocodingCandidate>> DueAsync(bool includeEmployees) =>
        _repository.GetDueCandidatesAsync(new AddressGeocodingDueQuery(
            includeEmployees, Now.AddDays(-30), Now.AddMinutes(-60), Unlimited));

    private async Task<Address> AddAddressAsync(
        EntityTypeEnum clientType,
        string zip = "8001",
        double? latitude = null,
        double? longitude = null,
        string state = "",
        bool deleted = false)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(),
            FirstName = "Geo",
            Name = Prefix + "Geocoding",
            Type = clientType,
            Gender = GenderEnum.Female
        };
        var address = new Address
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            ValidFrom = Now.AddYears(-1),
            Type = AddressTypeEnum.Employee,
            Street = "Teststrasse 1",
            Zip = zip,
            City = "Testort",
            Country = TestCountry,
            State = state,
            Latitude = latitude,
            Longitude = longitude,
            IsDeleted = deleted
        };
        _context.Client.Add(client);
        _context.Address.Add(address);
        await _context.SaveChangesAsync();
        if (deleted)
        {
            await _context.Database.ExecuteSqlInterpolatedAsync($"UPDATE address SET is_deleted = true WHERE id = {address.Id}");
        }

        _context.ChangeTracker.Clear();
        _ownIds.Add(address.Id);
        return address;
    }

    private async Task<Address> ReloadAsync(Guid addressId)
    {
        _context.ChangeTracker.Clear();
        return await _context.Address.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == addressId);
    }

    private static AddressGeocodingCandidate Candidate(Address address) => new(
        address.Id, address.ClientId, EntityTypeEnum.Customer, "x", address.Zip, address.City, address.Country, address.State);

    private static AddressGeocodingFill Fill(Address address, string? stateIfEmpty, Guid? userId = null) => new(
        address.Id, address.Zip, address.City, address.Country, 47.37, 8.54, stateIfEmpty,
        AddressGeocodingSource.NominatimLocality,
        new AddressGeocodingAttribution(AddressGeocodingTrigger.Assistant, userId),
        Now);

    private static GeocodedLocality Locality(GeocodingLocalityKey key, bool found, DateTime resolvedAt) => new()
    {
        CountryCode = key.CountryCode,
        PostalCode = key.PostalCode,
        City = key.City,
        Found = found,
        Latitude = found ? 47.37 : null,
        Longitude = found ? 8.54 : null,
        State = found ? "Zürich" : null,
        ResolvedAt = resolvedAt
    };

    private async Task CleanupAsync()
    {
        await _context.Database.ExecuteSqlRawAsync(
            $"DELETE FROM geocoded_locality WHERE starts_with(postal_code, '{Prefix}')");
        await _context.Database.ExecuteSqlRawAsync(
            $"DELETE FROM address WHERE client_id IN (SELECT id FROM client WHERE starts_with(name, '{Prefix}Geocoding'))");
        await _context.Database.ExecuteSqlRawAsync(
            $"DELETE FROM client WHERE starts_with(name, '{Prefix}Geocoding')");
        _context.ChangeTracker.Clear();
    }

    private static DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(TestHostDatabase.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }
}
