// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The macro assignment skills assign_macro_to_shift, assign_macro_to_absence_type and revert_macro_assignment against real
/// Postgres through the real gate, preview providers, planners, dry run service, mediator and handlers. A confirmation is
/// bound to the macro references its preview showed: a switch that is redeemed after a cut joined the order, a cut was
/// pointed at another macro or (for an undo) a younger switch exists writes nothing, spends the token and reports the
/// counts; an unchanged one writes exactly the previewed references and one history row each; a token sent along with the
/// call itself is refused and stays valid; a call without a binding never writes. The one double is the ISkillExecutor
/// handed to confirm_pending_action: an NSubstitute forwarder that passes the replay (skill name, parameters and the
/// context with the redeemed binding) straight to the real skill built in the same new scope, so everything it executes is
/// real. Every fixture row carries the INTEGRATION_TEST_Macro_ prefix and is removed by its own ids.
/// </summary>

using System.Security.Claims;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Assistant.Skills.Implementations;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.IntegrationTest.SignalR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using MacroEntity = Klacks.Api.Domain.Models.Settings.Macro;

namespace Klacks.IntegrationTest.Settings;

[TestFixture]
public class MacroAssignmentBindingIntegrationTests
{
    private const string SeedPrefix = "INTEGRATION_TEST_Macro_";
    private const string MacroScript = "OUTPUT 1, 0";
    private const string ConfirmationTokenKey = "confirmationToken";
    private const string AdminUserId = "integration-macro-admin";

    private SignalRTestWebApplicationFactory _factory = null!;
    private string _connectionString = null!;
    private DataBaseContext _context = null!;
    private Guid _confirmingUserId;
    private Guid _orderId;
    private Guid _firstCutId;
    private Guid _secondCutId;
    private Guid _standardMacroId;
    private Guid _macroAId;
    private Guid _macroBId;
    private readonly List<Guid> _shiftIds = new();
    private readonly List<Guid> _macroIds = new();
    private readonly List<Guid> _absenceIds = new();

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
        _factory = new SignalRTestWebApplicationFactory();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _factory?.Dispose();
    }

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _confirmingUserId = Guid.NewGuid();
        _shiftIds.Clear();
        _macroIds.Clear();
        _absenceIds.Clear();

        _standardMacroId = AddMacro("Standard", MacroCategoryEnum.Unspecified);
        _macroAId = AddMacro("A", MacroCategoryEnum.Unspecified);
        _macroBId = AddMacro("B", MacroCategoryEnum.Unspecified);
        _orderId = AddShift(ShiftStatus.SealedOrder, null, Guid.Empty);
        _firstCutId = AddShift(ShiftStatus.SplitShift, _orderId, _standardMacroId);
        _secondCutId = AddShift(ShiftStatus.SplitShift, _orderId, _standardMacroId);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM pending_confirmations WHERE user_id = {0}", _confirmingUserId);
        foreach (var id in _shiftIds.Concat(_absenceIds))
        {
            await _context.Database.ExecuteSqlRawAsync("DELETE FROM macro_assignment_history WHERE target_id = {0}", id);
        }

        foreach (var id in _shiftIds)
        {
            await _context.Database.ExecuteSqlRawAsync("DELETE FROM shift WHERE id = {0}", id);
        }

        foreach (var id in _absenceIds)
        {
            await _context.Database.ExecuteSqlRawAsync("DELETE FROM absence WHERE id = {0}", id);
        }

        foreach (var id in _macroIds)
        {
            await _context.Database.ExecuteSqlRawAsync("DELETE FROM macro WHERE id = {0}", id);
        }

        _context.Dispose();
    }

    [Test]
    public async Task Switch_ConfirmedReplayOfItsOwnPreview_WritesBothCutsAndOneHistoryRowEach()
    {
        var token = await HoldAsync(SwitchSkill, SwitchParameters(_firstCutId, _macroAId));

        var result = await RedeemAsync(token);

        result.Success.ShouldBeTrue(result.Message);
        (await MacroOfShiftAsync(_firstCutId)).ShouldBe(_macroAId);
        (await MacroOfShiftAsync(_secondCutId)).ShouldBe(_macroAId);
        var switchIds = await _context.Database
            .SqlQuery<Guid>($"SELECT DISTINCT switch_id AS \"Value\" FROM macro_assignment_history WHERE target_id IN ({_firstCutId}, {_secondCutId})")
            .ToListAsync();
        switchIds.Count.ShouldBe(1);
        (await HistoryCountAsync(_firstCutId, _secondCutId)).ShouldBe(2);
        (await PendingRowCountAsync(token)).ShouldBe(0);
    }

    [Test]
    public async Task Switch_ACutAddedAfterThePreview_WritesNothing_SpendsTheToken_AndTheNextCallIsHeldAgain()
    {
        var token = await HoldAsync(SwitchSkill, SwitchParameters(_firstCutId, _macroAId));
        AddShift(ShiftStatus.SplitShift, _orderId, _standardMacroId);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var result = await RedeemAsync(token);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("2 item(s) were shown, 3 apply now (1 new, 0 no longer there)");
        (await MacroOfShiftAsync(_firstCutId)).ShouldBe(_standardMacroId);
        (await MacroOfShiftAsync(_secondCutId)).ShouldBe(_standardMacroId);
        (await HistoryCountAsync(_firstCutId, _secondCutId)).ShouldBe(0);
        (await PendingRowCountAsync(token)).ShouldBe(0);

        var fresh = await HoldRawAsync(SwitchSkill, SwitchParameters(_firstCutId, _macroAId));
        fresh.Type.ShouldBe(SkillResultType.Confirmation);
        fresh.Message!.ShouldContain("and 2 other cut(s)");
    }

    [Test]
    public async Task Switch_ACutPointedAtAnotherMacroAfterThePreview_WritesNothing()
    {
        var token = await HoldAsync(SwitchSkill, SwitchParameters(_firstCutId, _macroAId));
        await _context.Database.ExecuteSqlRawAsync("UPDATE shift SET macro_id = {0} WHERE id = {1}", _macroBId, _secondCutId);

        var result = await RedeemAsync(token);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("(1 new, 1 no longer there)");
        (await MacroOfShiftAsync(_firstCutId)).ShouldBe(_standardMacroId);
        (await MacroOfShiftAsync(_secondCutId)).ShouldBe(_macroBId);
        (await HistoryCountAsync(_firstCutId, _secondCutId)).ShouldBe(0);
    }

    [Test]
    public async Task AbsenceType_ConfirmedReplay_WritesTheReferenceAndOneHistoryRow()
    {
        var vacationMacroId = AddMacro("Vacation", MacroCategoryEnum.Vacation);
        var absenceId = AddAbsence(_standardMacroId);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        var token = await HoldAsync(AbsenceSkill, new Dictionary<string, object>
        {
            [MacroAssignmentParameters.AbsenceTypeId] = absenceId.ToString(),
            [MacroAssignmentParameters.MacroId] = vacationMacroId.ToString()
        });

        var result = await RedeemAsync(token);

        result.Success.ShouldBeTrue(result.Message);
        (await _context.Database.SqlQuery<Guid>($"SELECT macro_id AS \"Value\" FROM absence WHERE id = {absenceId}").SingleAsync())
            .ShouldBe(vacationMacroId);
        (await HistoryCountAsync(absenceId)).ShouldBe(1);
    }

    [Test]
    public async Task Undo_ConfirmedReplay_PutsBothCutsBack_AndAnotherUndoOfTheSameSwitchIsRefusedAtThePreview()
    {
        (await RedeemAsync(await HoldAsync(SwitchSkill, SwitchParameters(_firstCutId, _macroAId)))).Success.ShouldBeTrue();
        var switchId = await LatestSwitchIdAsync(_firstCutId);
        var undoToken = await HoldAsync(RevertSkill, UndoParameters(switchId));

        var undone = await RedeemAsync(undoToken);

        undone.Success.ShouldBeTrue(undone.Message);
        (await MacroOfShiftAsync(_firstCutId)).ShouldBe(_standardMacroId);
        (await MacroOfShiftAsync(_secondCutId)).ShouldBe(_standardMacroId);
        (await HistoryCountAsync(_firstCutId, _secondCutId)).ShouldBe(4);
        await AssertEveryUndoRowPointsAtItsSwitchRowAsync(switchId);

        var again = await HoldRawAsync(RevertSkill, UndoParameters(switchId));
        again.Type.ShouldBe(SkillResultType.Error);
        again.Message!.ShouldContain("already undone");
    }

    [Test]
    public async Task Undo_AfterALaterSwitchOnTheSameCuts_IsRefusedByThePlanner_AndWritesNothing()
    {
        (await RedeemAsync(await HoldAsync(SwitchSkill, SwitchParameters(_firstCutId, _macroAId)))).Success.ShouldBeTrue();
        var switchId = await LatestSwitchIdAsync(_firstCutId);
        var undoToken = await HoldAsync(RevertSkill, UndoParameters(switchId));
        (await RedeemAsync(await HoldAsync(SwitchSkill, SwitchParameters(_firstCutId, _macroBId)))).Success.ShouldBeTrue();
        var rowsBefore = await HistoryCountAsync(_firstCutId, _secondCutId);

        var result = await RedeemAsync(undoToken);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("was switched again later");
        result.Message.ShouldContain("undo that switch first");
        result.Message.ShouldNotContain("What this confirmation was given for has changed");
        (await MacroOfShiftAsync(_firstCutId)).ShouldBe(_macroBId);
        (await MacroOfShiftAsync(_secondCutId)).ShouldBe(_macroBId);
        (await HistoryCountAsync(_firstCutId, _secondCutId)).ShouldBe(rowsBefore);
    }

    [Test]
    public async Task WithoutABinding_NoMacroSkillWrites_EvenWithTheGateBypassed()
    {
        using var scope = _factory.Services.CreateScope();
        ActAsAdmin(scope);
        var bypassed = TurnCtx(true) with { BypassAutonomyGate = true };

        var result = await Skill(scope, SwitchSkill).ExecuteAsync(bypassed, SwitchParameters(_firstCutId, _macroAId));
        var undo = await Skill(scope, RevertSkill).ExecuteAsync(bypassed, UndoParameters(Guid.NewGuid()));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("only runs as the confirmed replay");
        undo.Success.ShouldBeFalse();
        undo.Message.ShouldContain("only runs as the confirmed replay");
        (await MacroOfShiftAsync(_firstCutId)).ShouldBe(_standardMacroId);
        (await HistoryCountAsync(_firstCutId, _secondCutId)).ShouldBe(0);
    }

    [Test]
    public async Task ATokenSentWithTheCallItself_IsRefused_AndStillRedeemsThroughTheConfirmSkill()
    {
        var token = await HoldAsync(SwitchSkill, SwitchParameters(_firstCutId, _macroAId));
        var parameters = SwitchParameters(_firstCutId, _macroAId);
        parameters[AutonomyDefaults.ConfirmationTokenParameter] = token;

        using (var scope = _factory.Services.CreateScope())
        {
            ActAsAdmin(scope);
            var gate = scope.ServiceProvider.GetRequiredService<IAutonomyGate>();
            var direct = await gate.CheckAsync(Descriptor(SwitchSkill), TurnCtx(true), parameters);

            direct.ShouldNotBeNull();
            direct.Type.ShouldBe(SkillResultType.Error);
            direct.Message.ShouldBe(string.Format(ConfirmationBindingMessages.DirectRedemptionRefused, SwitchSkill));
        }

        (await MacroOfShiftAsync(_firstCutId)).ShouldBe(_standardMacroId);
        (await PendingRowCountAsync(token)).ShouldBe(1);
        (await RedeemAsync(token)).Success.ShouldBeTrue();
        (await MacroOfShiftAsync(_firstCutId)).ShouldBe(_macroAId);
    }

    private const string SwitchSkill = MacroAssignmentSkillNames.AssignToShift;
    private const string AbsenceSkill = MacroAssignmentSkillNames.AssignToAbsenceType;
    private const string RevertSkill = MacroAssignmentSkillNames.Revert;

    private static Dictionary<string, object> SwitchParameters(Guid shiftId, Guid macroId) => new()
    {
        [MacroAssignmentParameters.ShiftId] = shiftId.ToString(),
        [MacroAssignmentParameters.MacroId] = macroId.ToString()
    };

    private static Dictionary<string, object> UndoParameters(Guid switchId) => new()
    {
        [MacroAssignmentParameters.SwitchId] = switchId.ToString()
    };

    private static SkillDescriptor Descriptor(string name) =>
        new(name, "integration", SkillCategory.Crud, [], [], [], null);

    private SkillExecutionContext TurnCtx(bool explicitYes) => new()
    {
        UserId = _confirmingUserId,
        TenantId = Guid.NewGuid(),
        UserName = "integration-tester",
        UserLanguage = "en",
        UserPermissions = new List<string> { Roles.Admin },
        UserConfirmation = explicitYes ? UserConfirmationEvidence.ExplicitConfirmationReply : UserConfirmationEvidence.None
    };

    private static void ActAsAdmin(IServiceScope scope)
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, AdminUserId), new Claim(ClaimTypes.Role, Roles.Admin) },
            "IntegrationTest");
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext =
            new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    }

    private static BaseSkillImplementation Skill(IServiceScope scope, string skillName) => skillName switch
    {
        MacroAssignmentSkillNames.AssignToShift => ActivatorUtilities.CreateInstance<AssignMacroToShiftSkill>(scope.ServiceProvider),
        MacroAssignmentSkillNames.AssignToAbsenceType => ActivatorUtilities.CreateInstance<AssignMacroToAbsenceTypeSkill>(scope.ServiceProvider),
        _ => ActivatorUtilities.CreateInstance<RevertMacroAssignmentSkill>(scope.ServiceProvider)
    };

    private async Task<SkillResult> HoldRawAsync(string skillName, Dictionary<string, object> parameters)
    {
        using var scope = _factory.Services.CreateScope();
        ActAsAdmin(scope);
        var gate = scope.ServiceProvider.GetRequiredService<IAutonomyGate>();
        var held = await gate.CheckAsync(Descriptor(skillName), TurnCtx(false), parameters);
        held.ShouldNotBeNull();
        return held;
    }

    private async Task<string> HoldAsync(string skillName, Dictionary<string, object> parameters)
    {
        var held = await HoldRawAsync(skillName, parameters);
        held.Type.ShouldBe(SkillResultType.Confirmation, held.Message);
        return (string)held.Metadata![ConfirmationTokenKey];
    }

    private async Task<SkillResult> RedeemAsync(string token)
    {
        using var scope = _factory.Services.CreateScope();
        ActAsAdmin(scope);
        var executor = Substitute.For<ISkillExecutor>();
        executor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(call => Skill(scope, call.Arg<SkillInvocation>().SkillName).ExecuteAsync(
                call.Arg<SkillExecutionContext>(),
                call.Arg<SkillInvocation>().Parameters.ToDictionary(entry => entry.Key, entry => entry.Value)));
        var confirm = ActivatorUtilities.CreateInstance<ConfirmPendingActionSkill>(scope.ServiceProvider, executor);

        return await confirm.ExecuteAsync(
            TurnCtx(true), new Dictionary<string, object> { [AutonomyDefaults.ConfirmationTokenParameter] = token });
    }

    private async Task<Guid?> MacroOfShiftAsync(Guid shiftId) =>
        (await _context.Database.SqlQuery<Guid?>($"SELECT macro_id AS \"Value\" FROM shift WHERE id = {shiftId}").SingleAsync());

    private async Task<int> HistoryCountAsync(params Guid[] targetIds)
    {
        var total = 0;
        foreach (var id in targetIds)
        {
            total += await _context.Database
                .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM macro_assignment_history WHERE target_id = {id}")
                .SingleAsync();
        }

        return total;
    }

    private async Task<Guid> LatestSwitchIdAsync(Guid targetId) =>
        await _context.Database
            .SqlQuery<Guid>($"SELECT switch_id AS \"Value\" FROM macro_assignment_history WHERE target_id = {targetId} ORDER BY create_time DESC LIMIT 1")
            .SingleAsync();

    private async Task AssertEveryUndoRowPointsAtItsSwitchRowAsync(Guid switchId)
    {
        var links = await _context.Database
            .SqlQuery<HistoryLink>($@"SELECT s.target_id AS target_id, s.id AS switch_row_id, s.reverted_by_history_id AS reverted_by,
                    u.id AS undo_row_id, u.revert_of_history_id AS revert_of
                FROM macro_assignment_history s
                JOIN macro_assignment_history u ON u.target_id = s.target_id AND u.switch_id <> s.switch_id
                WHERE s.switch_id = {switchId} AND s.target_id IN ({_firstCutId}, {_secondCutId})")
            .ToListAsync();

        links.Count.ShouldBe(2);
        links.Select(link => link.TargetId).Distinct().Count().ShouldBe(2);
        foreach (var link in links)
        {
            link.RevertOf.ShouldBe(link.SwitchRowId);
            link.RevertedBy.ShouldBe(link.UndoRowId);
        }
    }

    private sealed class HistoryLink
    {
        public Guid TargetId { get; set; }

        public Guid SwitchRowId { get; set; }

        public Guid? RevertedBy { get; set; }

        public Guid UndoRowId { get; set; }

        public Guid? RevertOf { get; set; }
    }

    private async Task<int> PendingRowCountAsync(string token) =>
        await _context.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pending_confirmations WHERE token = {token}")
            .SingleAsync();

    private Guid AddMacro(string suffix, MacroCategoryEnum category)
    {
        var macro = new MacroEntity
        {
            Id = Guid.NewGuid(),
            Name = SeedPrefix + suffix,
            Content = MacroScript,
            Type = (int)MacroFunctionEnum.Custom,
            Category = category,
            Description = new MultiLanguage()
        };
        _context.Macro.Add(macro);
        _macroIds.Add(macro.Id);
        return macro.Id;
    }

    private Shift NewShift(ShiftStatus status, Guid? orderId, Guid macroId) => new()
    {
        Id = Guid.NewGuid(),
        Name = SeedPrefix + "Shift",
        Status = status,
        OriginalId = orderId,
        MacroId = macroId == Guid.Empty ? null : macroId,
        StartShift = new TimeOnly(8, 0),
        EndShift = new TimeOnly(16, 0)
    };

    private Guid AddShift(ShiftStatus status, Guid? orderId, Guid macroId)
    {
        var shift = NewShift(status, orderId, macroId);
        _context.Shift.Add(shift);
        _shiftIds.Add(shift.Id);
        return shift.Id;
    }

    private Guid AddAbsence(Guid macroId)
    {
        var absence = new Absence
        {
            Id = Guid.NewGuid(),
            Name = new MultiLanguage { De = SeedPrefix + "Absence" },
            Description = new MultiLanguage(),
            Abbreviation = new MultiLanguage(),
            MacroId = macroId
        };
        _context.Absence.Add(absence);
        _absenceIds.Add(absence.Id);
        return absence.Id;
    }
}
