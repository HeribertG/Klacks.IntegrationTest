// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// find_replacement through the real mediator pipeline against the integration database, for the rules a candidate
/// inherits from the order tree: a mandatory qualification set only on the sealed order excludes a candidate on a cut
/// piece, and the employee's day directives (schedule commands, combined cumulatively) exclude him. All rows carry the INTEGRATION_TEST_FRO_ marker and are removed prefix-scoped.
/// </summary>

using Klacks.Api.Application.Queries.Schedules;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.IntegrationTest.Wizard;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Recovery;

[TestFixture]
[Category("RealDatabase")]
[NonParallelizable]
public sealed class FindReplacementOrderRulesTests : WizardHarnessTestBase
{
    private const string Prefix = "INTEGRATION_TEST_FRO_";
    private static readonly DateOnly SlotDay = new(2091, 3, 7);
    private static readonly TimeOnly ShiftStart = new(8, 0);
    private static readonly TimeOnly ShiftEnd = new(16, 0);

    private Guid _groupId;
    private Guid _candidateId;
    private Guid _orderId;
    private Guid _pieceId;

    [SetUp]
    public async Task SeedSetUp()
    {
        await PurgeAsync();
        await SeedFixtureAsync();
    }

    [TearDown]
    public async Task SeedTearDown() => await PurgeAsync();

    [Test]
    public async Task Mandatory_Qualification_Of_The_Sealed_Order_Excludes_The_Candidate_On_A_Cut_Piece()
    {
        var before = await SendAsync(Query());
        before.Eligible.ShouldContain(c => c.ClientId == _candidateId, "precondition: without a requirement the candidate is clean");

        var qualification = new Qualification { Id = Guid.NewGuid(), Name = new MultiLanguage { De = Prefix + "QUAL" } };
        Context.Qualification.Add(qualification);
        Context.ShiftRequiredQualification.Add(new ShiftRequiredQualification
        {
            Id = Guid.NewGuid(),
            ShiftId = _orderId,
            QualificationId = qualification.Id,
            IsMandatory = true,
            MinLevel = QualificationLevel.Basic,
        });
        await Context.SaveChangesAsync();

        var after = await SendAsync(Query());

        after.Eligible.ShouldNotContain(c => c.ClientId == _candidateId);
        after.Excluded.ShouldContain(
            c => c.ClientId == _candidateId && c.Reason == QualificationValidationKeys.Missing,
            "the piece inherits the order's mandatory qualification, the candidate does not hold it");
    }

    [Test]
    public async Task Free_Directive_Excludes_The_Candidate_With_The_Directive_Reason()
    {
        var tokens = await KeywordTokensAsync();
        await AddCommandAsync(tokens.FreeToken);

        var result = await SendAsync(Query());

        result.Eligible.ShouldNotContain(c => c.ClientId == _candidateId);
        result.Excluded.ShouldContain(
            c => c.ClientId == _candidateId && c.Reason == ScheduleValidationKeys.DayDirective,
            "find_replacement must not propose an employee against a FREE directive");
    }

    [Test]
    public async Task Directives_Of_A_Day_Combine_Cumulatively()
    {
        var tokens = await KeywordTokensAsync();
        await AddCommandAsync(tokens.NegEarlyToken);
        await AddCommandAsync(tokens.NegNightToken);

        var onlyLate = await SendAsync(Query());
        onlyLate.Eligible.ShouldContain(c => c.ClientId == _candidateId, "-EARLY and -NIGHT leave the late kind open (08:00-16:00 reaches the late band)");

        await AddCommandAsync(tokens.NegLateToken);

        var closed = await SendAsync(Query());
        closed.Excluded.ShouldContain(
            c => c.ClientId == _candidateId && c.Reason == ScheduleValidationKeys.DayDirective,
            "-LATE on top of -EARLY and -NIGHT closes the day");
    }

    private async Task<Klacks.Api.Domain.Models.Schedules.ScheduleCommandKeywordSet> KeywordTokensAsync()
    {
        using var scope = CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<Klacks.Api.Domain.Interfaces.Schedules.IScheduleCommandKeywordProvider>()
            .GetAsync(CancellationToken.None);
    }

    private async Task AddCommandAsync(string keyword)
    {
        Context.Set<ScheduleCommand>().Add(new ScheduleCommand
        {
            Id = Guid.NewGuid(),
            ClientId = _candidateId,
            CurrentDate = SlotDay,
            CommandKeyword = keyword,
        });
        await Context.SaveChangesAsync();
    }

    private FindReplacementQuery Query() => new(
        ShiftId: _pieceId,
        Date: SlotDay,
        StartTime: ShiftStart,
        EndTime: ShiftEnd,
        GroupId: _groupId,
        AnalyseToken: null);

    private async Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request)
    {
        using var scope = CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        return await mediator.Send(request, CancellationToken.None);
    }

    private async Task SeedFixtureAsync()
    {
        _groupId = Guid.NewGuid();
        _candidateId = Guid.NewGuid();
        _orderId = Guid.NewGuid();
        _pieceId = Guid.NewGuid();
        var validFrom = new DateTime(2091, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Context.Group.Add(new Group { Id = _groupId, Name = Prefix + "GROUP", ValidFrom = validFrom });
        Context.Client.Add(new Client { Id = _candidateId, FirstName = Prefix, Name = Prefix + "Candidate", Type = EntityTypeEnum.Employee });
        var contractId = Guid.NewGuid();
        Context.Contract.Add(new Contract
        {
            Id = contractId,
            Name = Prefix + "Contract",
            ValidFrom = validFrom,
            GuaranteedHours = 100m,
            MaximumHours = 200m,
            FullTime = 40m,
            PerformsShiftWork = true,
            WorkOnMonday = true,
            WorkOnTuesday = true,
            WorkOnWednesday = true,
            WorkOnThursday = true,
            WorkOnFriday = true,
            WorkOnSaturday = true,
            WorkOnSunday = true,
        });
        Context.ClientContract.Add(new ClientContract
        {
            Id = Guid.NewGuid(),
            ClientId = _candidateId,
            ContractId = contractId,
            FromDate = new DateOnly(2091, 1, 1),
            IsActive = true,
        });
        Context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), ClientId = _candidateId, GroupId = _groupId, ValidFrom = validFrom });

        Context.Shift.Add(NewShift(_orderId, ShiftStatus.SealedOrder, null));
        Context.Shift.Add(NewShift(_pieceId, ShiftStatus.SplitShift, _orderId));
        Context.GroupItem.Add(new GroupItem { Id = Guid.NewGuid(), ShiftId = _pieceId, GroupId = _groupId, ValidFrom = validFrom });

        await Context.SaveChangesAsync();
    }

    private static Shift NewShift(Guid id, ShiftStatus status, Guid? originalId) => new()
    {
        Id = id,
        Name = Prefix + "SHIFT",
        Abbreviation = "ITFRO",
        StartShift = ShiftStart,
        EndShift = ShiftEnd,
        WorkTime = 8m,
        FromDate = new DateOnly(2091, 1, 1),
        IsMonday = true,
        IsTuesday = true,
        IsWednesday = true,
        IsThursday = true,
        IsFriday = true,
        IsSaturday = true,
        IsSunday = true,
        Quantity = 1,
        Status = status,
        OriginalId = originalId,
    };

    /// <summary>
    /// Prefix-scoped hard cleanup, FK children first. Only rows carrying the INTEGRATION_TEST_FRO_ marker or
    /// referencing such rows are deleted.
    /// </summary>
    private async Task PurgeAsync()
    {
        var sql = $@"
            DELETE FROM schedule_commands WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM work WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM group_item WHERE group_id IN (SELECT id FROM ""group"" WHERE name LIKE '{Prefix}%');
            DELETE FROM group_item WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{Prefix}%');
            DELETE FROM group_item WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM shift_required_qualification WHERE shift_id IN (SELECT id FROM shift WHERE name LIKE '{Prefix}%');
            DELETE FROM qualification WHERE name->>'de' LIKE '{Prefix}%';
            DELETE FROM shift WHERE name LIKE '{Prefix}%';
            DELETE FROM ""group"" WHERE name LIKE '{Prefix}%';
            DELETE FROM client_contract WHERE client_id IN (SELECT id FROM client WHERE first_name LIKE '{Prefix}%');
            DELETE FROM contract WHERE name LIKE '{Prefix}%';
            DELETE FROM client WHERE first_name LIKE '{Prefix}%';
        ";
        await Context.Database.ExecuteSqlRawAsync(sql);
    }
}
