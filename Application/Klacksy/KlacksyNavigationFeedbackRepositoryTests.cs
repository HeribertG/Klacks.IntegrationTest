// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.IntegrationTest.Application.Klacksy;

using Shouldly;
using Klacks.Api.Domain.Models.Klacksy;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Klacksy;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;

[TestFixture]
[Category("RealDatabase")]
public class KlacksyNavigationFeedbackRepositoryTests
{
    private DataBaseContext _context = null!;
    private KlacksyNavigationFeedbackRepository _repo = null!;
    private readonly List<Guid> _insertedIds = [];

    [SetUp]
    public void SetUp()
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(connectionString)
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _repo = new KlacksyNavigationFeedbackRepository(_context);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_insertedIds.Count > 0)
        {
            await _context.KlacksyNavigationFeedback
                .Where(e => _insertedIds.Contains(e.Id))
                .ExecuteDeleteAsync();
        }

        _context.Dispose();
        _insertedIds.Clear();
    }

    [Test]
    public async Task Add_persists_feedback_and_QueryUnresolved_returns_it()
    {
        var entity = new KlacksyNavigationFeedback
        {
            Utterance = "where is ai setting",
            Locale = "en",
            MatchedTargetId = null,
            UserAction = "gave-up"
        };

        await _repo.AddAsync(entity, CancellationToken.None);
        _insertedIds.Add(entity.Id);

        var unresolved = await _repo.QueryUnresolvedAsync("en", 10, CancellationToken.None);
        unresolved.ShouldContain(x => x.Utterance == "where is ai setting");
    }
}
