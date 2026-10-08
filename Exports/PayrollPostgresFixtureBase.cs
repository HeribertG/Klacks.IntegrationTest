// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Shared plumbing of the payroll export PostgreSQL integration tests: a DbContext on the integration-test database
/// (pending migrations applied first), builders for test-owned persons, shifts, groups, work rows and day seals, and a
/// cleanup that removes exactly the rows the test created. Every name, reason and actor carries the
/// INTEGRATION_TEST_ prefix; every delete is keyed by the ids the fixture itself generated or by that prefix, never by
/// business-plausible values. The test period lies in 2037 so that no real plan data falls inside it.
/// </summary>
using System.Text;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.Exports;

public abstract class PayrollPostgresFixtureBase
{
    protected const string Prefix = "INTEGRATION_TEST_PayrollWp0b_";
    protected const string Actor = Prefix + "actor";

    protected static readonly DateOnly Day1 = new(2037, 3, 2);
    protected static readonly DateOnly Day2 = Day1.AddDays(1);
    protected static readonly DateOnly Day3 = Day1.AddDays(2);

    private readonly List<Guid> _clientIds = [];
    private readonly List<Guid> _groupIds = [];
    private readonly List<Guid> _shiftIds = [];
    private readonly List<Guid> _workIds = [];
    private readonly List<Guid> _sealedDayIds = [];
    private readonly List<Guid> _exportLogIds = [];

    protected DataBaseContext Context { get; private set; } = null!;

    protected Guid GroupId { get; private set; }

    protected Guid ShiftId { get; private set; }

    [OneTimeSetUp]
    public void RegisterEncodings()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [SetUp]
    public async Task SetUpDatabase()
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        Context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        await Context.Database.MigrateAsync();

        GroupId = AddGroup("Group");
        ShiftId = AddShift(GroupId);
        await Context.SaveChangesAsync();
    }

    [TearDown]
    public async Task CleanUpDatabase()
    {
        Context.ChangeTracker.Clear();
        await Context.ExportLogItem.IgnoreQueryFilters()
            .Where(i => _clientIds.Contains(i.ClientId) || _exportLogIds.Contains(i.ExportLogId))
            .ExecuteDeleteAsync();
        await Context.ExportLog.IgnoreQueryFilters()
            .Where(l => _exportLogIds.Contains(l.Id) || l.ExportedBy.StartsWith(Prefix))
            .ExecuteDeleteAsync();
        await Context.SealedDay.IgnoreQueryFilters()
            .Where(s => _sealedDayIds.Contains(s.Id) || (s.Reason != null && s.Reason.StartsWith(Prefix)))
            .ExecuteDeleteAsync();
        await Context.Work.IgnoreQueryFilters().Where(w => _workIds.Contains(w.Id)).ExecuteDeleteAsync();
        await Context.GroupItem.IgnoreQueryFilters()
            .Where(g => _groupIds.Contains(g.GroupId) || (g.ClientId != null && _clientIds.Contains(g.ClientId.Value)))
            .ExecuteDeleteAsync();
        await Context.Membership.IgnoreQueryFilters().Where(m => _clientIds.Contains(m.ClientId)).ExecuteDeleteAsync();
        await Context.Group.IgnoreQueryFilters().Where(g => _groupIds.Contains(g.Id)).ExecuteDeleteAsync();
        await Context.Shift.IgnoreQueryFilters().Where(s => _shiftIds.Contains(s.Id)).ExecuteDeleteAsync();
        await Context.Client.IgnoreQueryFilters().Where(c => _clientIds.Contains(c.Id)).ExecuteDeleteAsync();
        await Context.DisposeAsync();
    }

    protected void TrackExportLog(Guid exportLogId)
    {
        _exportLogIds.Add(exportLogId);
    }

    protected Guid AddGroup(string suffix)
    {
        var id = Guid.NewGuid();
        Context.Group.Add(new Group
        {
            Id = id,
            Name = Prefix + suffix + "_" + id.ToString("N")[..8],
            Description = "Payroll export integration test group",
            ValidFrom = DateTime.UtcNow.AddYears(-1),
        });
        _groupIds.Add(id);
        return id;
    }

    protected Guid AddShift(Guid groupId)
    {
        var id = Guid.NewGuid();
        Context.Shift.Add(new Shift
        {
            Id = id,
            Name = Prefix + "Shift_" + id.ToString("N")[..8],
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
        });
        Context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ShiftId = id });
        _shiftIds.Add(id);
        return id;
    }

    protected Guid AddPerson(string label, Guid? memberOfGroup = null)
    {
        var id = Guid.NewGuid();
        Context.Client.Add(new Client
        {
            Id = id,
            Name = Prefix + label + "_" + id.ToString("N")[..8],
            FirstName = "Payroll",
            Type = EntityTypeEnum.Employee,
        });
        _clientIds.Add(id);

        if (memberOfGroup.HasValue)
        {
            Context.Membership.Add(new Membership
            {
                Id = Guid.NewGuid(),
                ClientId = id,
                ValidFrom = new DateTime(2036, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            Context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), GroupId = memberOfGroup.Value, ClientId = id });
        }

        return id;
    }

    protected Guid AddUngroupedShift()
    {
        var id = Guid.NewGuid();
        Context.Shift.Add(new Shift
        {
            Id = id,
            Name = Prefix + "UngroupedShift_" + id.ToString("N")[..8],
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(16, 0),
        });
        _shiftIds.Add(id);
        return id;
    }

    protected Guid AddWork(Guid clientId, DateOnly date, decimal hours, WorkLockLevel lockLevel, Guid? shiftId = null)
    {
        var id = Guid.NewGuid();
        Context.Work.Add(new Work
        {
            Id = id,
            ClientId = clientId,
            ShiftId = shiftId ?? ShiftId,
            CurrentDate = date,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
            WorkTime = hours,
            LockLevel = lockLevel,
        });
        _workIds.Add(id);
        return id;
    }

    protected void AddSeal(DateOnly date, Guid? groupId, WorkLockLevel level = WorkLockLevel.Closed)
    {
        var id = Guid.NewGuid();
        Context.SealedDay.Add(new SealedDay
        {
            Id = id,
            Date = date,
            GroupId = groupId,
            Level = level,
            Reason = Prefix + "seal",
            SealedAt = DateTime.UtcNow,
            SealedBy = Actor,
        });
        _sealedDayIds.Add(id);
    }

    protected void SealPeriod(Guid? groupId)
    {
        AddSeal(Day1, groupId);
        AddSeal(Day2, groupId);
        AddSeal(Day3, groupId);
    }
}
