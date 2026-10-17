// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// UserExperienceSignalReader against real PostgreSQL: works are stamped through the real SaveChanges audit path
/// (NameIdentifier claim = user id), so the reader's CurrentUserCreated match is proven on the actual stamp format;
/// only the user's live real-plan works count (no scenario rows, no soft-deleted rows, no other user's rows), the
/// span comes from their CreateTime, and only successful Crud/Action skill executions of the user count as writes.
/// All rows belong to two random user ids and are removed by those ids afterwards.
/// </summary>

using System.Security.Claims;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant;

[TestFixture]
[Category("RealDatabase")]
public class UserExperienceSignalReaderNpgsqlTests
{
    private const string TestSkillName = "INTEGRATION_TEST_experience_signal";

    private static readonly DateTime FirstCreated = new(2026, 1, 5, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LastCreated = new(2026, 3, 9, 17, 30, 0, DateTimeKind.Utc);

    private string _connectionString = null!;
    private Guid _userId;
    private Guid _otherUserId;
    private Guid _clientId;
    private Guid _shiftId;

    private DataBaseContext NewContext(Guid? actingUser = null)
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        var accessor = Substitute.For<IHttpContextAccessor>();
        if (actingUser.HasValue)
        {
            var httpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, actingUser.Value.ToString())], "test"))
            };
            accessor.HttpContext.Returns(httpContext);
        }

        return new DataBaseContext(options, accessor);
    }

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        await using var ctx = NewContext();
        _clientId = await ctx.Client.Select(c => c.Id).FirstOrDefaultAsync();
        _shiftId = await ctx.Shift.Select(s => s.Id).FirstOrDefaultAsync();
        if (_clientId == Guid.Empty || _shiftId == Guid.Empty)
        {
            Assert.Ignore("The test database has no client or no shift to attach works to.");
        }
    }

    [SetUp]
    public void SetUp()
    {
        _userId = Guid.NewGuid();
        _otherUserId = Guid.NewGuid();
    }

    [TearDown]
    public async Task TearDown()
    {
        await using var ctx = NewContext();
        var stamps = new[] { _userId.ToString(), _otherUserId.ToString() };
        await ctx.Work.IgnoreQueryFilters().Where(w => stamps.Contains(w.CurrentUserCreated)).ExecuteDeleteAsync();
        await ctx.SkillUsageRecords.IgnoreQueryFilters()
            .Where(r => r.UserId == _userId || r.UserId == _otherUserId)
            .ExecuteDeleteAsync();
    }

    private Work NewWork(int dayOffset, Guid? analyseToken = null) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = _clientId,
        ShiftId = _shiftId,
        CurrentDate = new DateOnly(2001, 1, 1).AddDays(dayOffset),
        StartTime = new TimeOnly(8, 0),
        EndTime = new TimeOnly(16, 0),
        WorkTime = 8,
        AnalyseToken = analyseToken
    };

    private SkillUsageRecord NewUsage(Guid userId, SkillCategory category, bool success) => new()
    {
        Id = Guid.NewGuid(),
        SkillName = TestSkillName,
        Category = category,
        UserId = userId,
        TenantId = Guid.Empty,
        Success = success,
        Timestamp = DateTime.UtcNow
    };

    [Test]
    public async Task ReadAsync_CountsOnlyTheUsersLiveRealPlanWorks_AndSuccessfulWrites()
    {
        var oldest = NewWork(0);
        var middle = NewWork(1);
        var newest = NewWork(2);
        var deleted = NewWork(3);

        await using (var userCtx = NewContext(_userId))
        {
            userCtx.Work.AddRange(oldest, middle, newest, deleted, NewWork(4, analyseToken: Guid.NewGuid()));
            userCtx.SkillUsageRecords.AddRange(
                NewUsage(_userId, SkillCategory.Crud, success: true),
                NewUsage(_userId, SkillCategory.Action, success: true),
                NewUsage(_userId, SkillCategory.Query, success: true),
                NewUsage(_userId, SkillCategory.Crud, success: false));
            await userCtx.SaveChangesAsync();
        }

        await using (var otherCtx = NewContext(_otherUserId))
        {
            otherCtx.Work.Add(NewWork(5));
            otherCtx.SkillUsageRecords.Add(NewUsage(_otherUserId, SkillCategory.Crud, success: true));
            await otherCtx.SaveChangesAsync();
        }

        await using (var fixCtx = NewContext())
        {
            await fixCtx.Work.Where(w => w.Id == oldest.Id).ExecuteUpdateAsync(s => s.SetProperty(w => w.CreateTime, FirstCreated));
            await fixCtx.Work.Where(w => w.Id == middle.Id).ExecuteUpdateAsync(s => s.SetProperty(w => w.CreateTime, FirstCreated.AddDays(20)));
            await fixCtx.Work.Where(w => w.Id == newest.Id).ExecuteUpdateAsync(s => s.SetProperty(w => w.CreateTime, LastCreated));
            await fixCtx.Work.Where(w => w.Id == deleted.Id).ExecuteUpdateAsync(s => s
                .SetProperty(w => w.IsDeleted, true)
                .SetProperty(w => w.CreateTime, LastCreated.AddDays(100)));
        }

        await using var readCtx = NewContext();
        var signals = await new UserExperienceSignalReader(readCtx).ReadAsync(_userId);

        signals.OwnWorkCount.ShouldBe(3);
        signals.FirstWorkCreatedUtc.ShouldBe(FirstCreated);
        signals.LastWorkCreatedUtc.ShouldBe(LastCreated);
        signals.ActiveSpanDays.ShouldBe((int)(LastCreated - FirstCreated).TotalDays);
        signals.SuccessfulSkillWrites.ShouldBe(2);
    }

    [Test]
    public async Task ReadAsync_UserWithoutRows_ReturnsEmptySignals()
    {
        await using var readCtx = NewContext();

        var signals = await new UserExperienceSignalReader(readCtx).ReadAsync(_userId);

        signals.ShouldBe(UserExperienceSignals.None);
    }
}
