// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Read-only integration tests for the DATEV payroll export against the real database: they verify that
/// PayrollExportDataLoader's seal-mirror query and day-granular projection reproduce an independently
/// computed aggregate over the same closed Work rows, and that DatevLugBewegungsdatenFormatter turns that
/// projection into a well-formed 11-field Windows-1252 file. No rows are written or deleted.
/// </summary>

using System.Text;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Exports.Payroll;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.Exports;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Exports;

[TestFixture]
[Category("RealDatabase")]
public class PayrollExportRealDataTests
{
    private const string BaseWageType = "1000";
    private const string SurchargeWageType = "1500";

    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private PayrollExportDataLoader _loader = null!;

    private Guid _groupId;
    private DateOnly _fromDate;
    private DateOnly _untilDate;
    private decimal _expectedHours;
    private decimal _expectedSurcharges;
    private int _expectedEmployeeCount;
    private bool _hasData;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
    }

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _loader = new PayrollExportDataLoader(_context);

        await DiscoverClosedGroupAsync();
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    private async Task DiscoverClosedGroupAsync()
    {
        var candidate = await _context.Work
            .AsNoTracking()
            .Where(w => !w.IsDeleted
                && w.AnalyseToken == null
                && w.LockLevel == WorkLockLevel.Closed
                && w.Client != null
                && (w.Client.Type == EntityTypeEnum.Employee || w.Client.Type == EntityTypeEnum.ExternEmp)
                && _context.GroupItem.Any(gi => gi.ShiftId == w.ShiftId && !gi.IsDeleted))
            .SelectMany(w => _context.GroupItem
                .Where(gi => gi.ShiftId == w.ShiftId && !gi.IsDeleted)
                .Select(gi => new { gi.GroupId, w.CurrentDate }))
            .GroupBy(x => x.GroupId)
            .Select(g => new
            {
                GroupId = g.Key,
                Count = g.Count(),
                Min = g.Min(x => x.CurrentDate),
                Max = g.Max(x => x.CurrentDate),
            })
            .OrderByDescending(g => g.Count)
            .FirstOrDefaultAsync();

        if (candidate == null)
        {
            _hasData = false;
            return;
        }

        _groupId = candidate.GroupId;
        _fromDate = candidate.Min;
        _untilDate = candidate.Max;

        var scoped = _context.Work
            .AsNoTracking()
            .Where(w => !w.IsDeleted
                && w.AnalyseToken == null
                && w.LockLevel == WorkLockLevel.Closed
                && w.CurrentDate >= _fromDate
                && w.CurrentDate <= _untilDate
                && w.Client != null
                && (w.Client.Type == EntityTypeEnum.Employee || w.Client.Type == EntityTypeEnum.ExternEmp)
                && _context.GroupItem.Any(gi => gi.ShiftId == w.ShiftId && gi.GroupId == _groupId && !gi.IsDeleted));

        _expectedHours = await scoped.SumAsync(w => w.WorkTime);
        _expectedSurcharges = await scoped.SumAsync(w => w.Surcharges);
        _expectedEmployeeCount = await scoped.Select(w => w.ClientId).Distinct().CountAsync();
        _hasData = _expectedEmployeeCount > 0;
    }

    [Test]
    public async Task LoadAsync_ProjectsClosedWorkIntoEmployeesMatchingIndependentAggregate()
    {
        if (!_hasData)
        {
            Assert.Ignore("No closed employee Work rows in any group — cannot exercise the real-data payroll path.");
        }

        var data = await _loader.LoadAsync(_groupId, _fromDate, _untilDate);

        data.Employees.Count.ShouldBe(_expectedEmployeeCount);

        var projectedHours = data.Employees
            .SelectMany(e => e.Entries)
            .Where(e => e.Kind == PayrollEntryKind.WorkHours)
            .Sum(e => e.Quantity);
        var projectedSurcharges = data.Employees
            .SelectMany(e => e.Entries)
            .Where(e => e.Kind == PayrollEntryKind.Surcharge)
            .Sum(e => e.Quantity);

        projectedHours.ShouldBe(_expectedHours);
        projectedSurcharges.ShouldBe(_expectedSurcharges);

        data.Employees.ShouldAllBe(e => !string.IsNullOrWhiteSpace(e.FullName));
    }

    [Test]
    public async Task Format_RealClosedData_ProducesWellFormedDatevLugFile()
    {
        if (!_hasData)
        {
            Assert.Ignore("No closed employee Work rows in any group — cannot exercise the real-data payroll path.");
        }

        var data = await _loader.LoadAsync(_groupId, _fromDate, _untilDate);

        var config = new PayrollExportGroupConfig
        {
            GroupId = _groupId,
            TargetSystem = PayrollExportConstants.FormatKeyDatevLug,
            Delimiter = PayrollExportConstants.DefaultDelimiter,
            Encoding = PayrollExportConstants.DefaultEncoding,
            BaseWageType = BaseWageType,
            SurchargeWageType = SurchargeWageType,
            AbsenceMappingJson = "{}",
        };

        var formatter = new DatevLugBewegungsdatenFormatter();
        var result = formatter.Format(data, config);

        result.RecordCount.ShouldBeGreaterThan(0);
        result.Content.Length.ShouldBeGreaterThan(0);

        var text = Encoding.GetEncoding(PayrollExportConstants.Windows1252CodePage).GetString(result.Content);
        var lines = text.Split(PayrollExportConstants.LineEnding, StringSplitOptions.RemoveEmptyEntries);

        lines.Length.ShouldBe(result.RecordCount);
        lines.ShouldAllBe(line =>
            line.Split(PayrollExportConstants.DefaultDelimiter).Length == PayrollExportConstants.DatevLugFieldCount);

        var baseRows = lines.Count(line =>
            line.Split(PayrollExportConstants.DefaultDelimiter)[3] == BaseWageType);
        baseRows.ShouldBeGreaterThan(0);
    }
}
