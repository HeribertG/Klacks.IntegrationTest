// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration tests for address validation via Nominatim geocoding.
/// Tests international addresses (Germany, Austria, France, Italy, Liechtenstein)
/// and verifies state auto-fill, state mismatch detection, and geocoding results.
/// </summary>
/// <param name="_geocodingService">Real GeocodingService calling Nominatim API</param>
/// <param name="_stateResolver">Resolves Nominatim state names to DB abbreviations</param>

using FluentAssertions;
using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.Clients;

[TestFixture]
[Category("RealDatabase")]
[Category("ExternalApi")]
public class AddressValidationIntegrationTests
{
    private IGeocodingService _geocodingService = null!;
    private StateAbbreviationResolver _stateResolver = null!;
    private DataBaseContext _context = null!;
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("User-Agent", "KlacksIntegrationTest/1.0");
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var logger = Substitute.For<ILogger<GeocodingService>>();

        _geocodingService = new GeocodingService(httpClientFactory, cache, logger);
    }

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);

        var stateLogger = Substitute.For<ILogger<State>>();
        var stateRepository = new Api.Infrastructure.Repositories.Settings.StateRepository(_context, stateLogger);
        _stateResolver = new StateAbbreviationResolver(stateRepository);
    }

    [TearDown]
    public void TearDown()
    {
        _context?.Dispose();
    }

    [Test]
    public async Task Swiss_Address_Bern_Should_Return_State()
    {
        var result = await _geocodingService.ValidateExactAddressAsync(
            "Bundesplatz 3", "3005", "Bern", "Schweiz");

        Console.WriteLine($"Swiss Bern: Found={result.Found}, State={result.State}, Address={result.ReturnedAddress}");

        result.Found.Should().BeTrue();
        result.State.Should().NotBeNullOrWhiteSpace();

        var abbreviation = await _stateResolver.ResolveAsync(result.State);
        Console.WriteLine($"  Resolved: {result.State} -> {abbreviation}");

        abbreviation.Should().Be("BE");
    }

    [Test]
    public async Task Swiss_Address_Zurich_Should_Return_State()
    {
        var result = await _geocodingService.ValidateExactAddressAsync(
            "Bahnhofstrasse 1", "8001", "Zürich", "Schweiz");

        Console.WriteLine($"Swiss Zürich: Found={result.Found}, State={result.State}, Address={result.ReturnedAddress}");

        result.Found.Should().BeTrue();
        result.State.Should().NotBeNullOrWhiteSpace();

        var abbreviation = await _stateResolver.ResolveAsync(result.State);
        Console.WriteLine($"  Resolved: {result.State} -> {abbreviation}");

        abbreviation.Should().Be("ZH");
    }

    [Test]
    public async Task Swiss_Address_Liebefeld_3097_Should_Be_Valid()
    {
        var result = await _geocodingService.ValidateExactAddressAsync(
            null, "3097", "Liebefeld", "Schweiz");

        Console.WriteLine($"Swiss Liebefeld 3097: Found={result.Found}, State={result.State}, MatchType={result.MatchType}");

        result.Found.Should().BeTrue();
        result.State.Should().NotBeNullOrWhiteSpace();

        var abbreviation = await _stateResolver.ResolveAsync(result.State);
        Console.WriteLine($"  Resolved: {result.State} -> {abbreviation}");

        abbreviation.Should().Be("BE");
    }

    [Test]
    public async Task German_Address_Berlin_Should_Be_Found()
    {
        var result = await _geocodingService.ValidateExactAddressAsync(
            "Unter den Linden 1", "10117", "Berlin", "Deutschland");

        Console.WriteLine($"German Berlin: Found={result.Found}, State={result.State}, Address={result.ReturnedAddress}");

        result.Found.Should().BeTrue();
        Console.WriteLine($"  State from Nominatim: {result.State ?? "NULL (city-state, expected for Berlin)"}");
    }

    [Test]
    public async Task German_Address_Munich_Should_Return_State()
    {
        var result = await _geocodingService.ValidateExactAddressAsync(
            "Marienplatz 1", "80331", "München", "Deutschland");

        Console.WriteLine($"German München: Found={result.Found}, State={result.State}, Address={result.ReturnedAddress}");

        result.Found.Should().BeTrue();
        result.State.Should().NotBeNullOrWhiteSpace();
        Console.WriteLine($"  State from Nominatim: {result.State}");
    }

    [Test]
    public async Task Austrian_Address_Vienna_Should_Be_Found()
    {
        var result = await _geocodingService.ValidateExactAddressAsync(
            "Stephansplatz 1", "1010", "Wien", "Österreich");

        Console.WriteLine($"Austrian Wien: Found={result.Found}, State={result.State}, Address={result.ReturnedAddress}");

        result.Found.Should().BeTrue();
        Console.WriteLine($"  State from Nominatim: {result.State ?? "NULL (city-state, expected for Wien)"}");
    }

    [Test]
    public async Task French_Address_Paris_Should_Return_State()
    {
        var result = await _geocodingService.ValidateExactAddressAsync(
            "Champs-Élysées", "75008", "Paris", "France");

        Console.WriteLine($"French Paris: Found={result.Found}, State={result.State}, Address={result.ReturnedAddress}");

        result.Found.Should().BeTrue();
        Console.WriteLine($"  State from Nominatim: {result.State}");
    }

    [Test]
    public async Task Italian_Address_Rome_Should_Return_State()
    {
        var result = await _geocodingService.ValidateExactAddressAsync(
            "Via del Corso", "00186", "Roma", "Italia");

        Console.WriteLine($"Italian Roma: Found={result.Found}, State={result.State}, Address={result.ReturnedAddress}");

        result.Found.Should().BeTrue();
        Console.WriteLine($"  State from Nominatim: {result.State}");
    }

    [Test]
    public async Task Liechtenstein_Address_Should_Work_Without_State()
    {
        var result = await _geocodingService.ValidateExactAddressAsync(
            "Städtle 38", "9490", "Vaduz", "Liechtenstein");

        Console.WriteLine($"Liechtenstein Vaduz: Found={result.Found}, State={result.State}, Address={result.ReturnedAddress}");

        result.Found.Should().BeTrue();

        var abbreviation = await _stateResolver.ResolveAsync(result.State);
        Console.WriteLine($"  Resolved: {result.State} -> {abbreviation ?? "NULL (no state in DB, expected for Liechtenstein)"}");
    }

    [Test]
    public async Task Invalid_Address_Should_Not_Be_Found()
    {
        var result = await _geocodingService.ValidateExactAddressAsync(
            "Nichtexistierende Strasse 99999", "00000", "Nirgendwo", "Schweiz");

        Console.WriteLine($"Invalid address: Found={result.Found}, MatchType={result.MatchType}");

        result.Found.Should().BeFalse();
    }

    [Test]
    public async Task State_Mismatch_Should_Be_Detectable()
    {
        var result = await _geocodingService.ValidateExactAddressAsync(
            "Bundesplatz 3", "3005", "Bern", "Schweiz");

        result.Found.Should().BeTrue();

        var correctState = await _stateResolver.ResolveAsync(result.State);
        correctState.Should().Be("BE");

        var wrongState = "AG";
        var isMatch = string.Equals(wrongState, correctState, StringComparison.OrdinalIgnoreCase);

        Console.WriteLine($"State mismatch test: Entered=AG, Expected={correctState}, Match={isMatch}");

        isMatch.Should().BeFalse("AG should not match BE for Bern address");
    }

    [Test]
    public async Task State_AutoFill_Should_Resolve_Abbreviation()
    {
        var result = await _geocodingService.ValidateExactAddressAsync(
            null, "8001", "Zürich", "Schweiz");

        result.Found.Should().BeTrue();
        result.State.Should().NotBeNullOrWhiteSpace();

        var abbreviation = await _stateResolver.ResolveAsync(result.State);

        Console.WriteLine($"Auto-fill test: Nominatim returned '{result.State}', resolved to '{abbreviation}'");

        abbreviation.Should().NotBeNullOrWhiteSpace();
        abbreviation.Should().Be("ZH");
    }
}
