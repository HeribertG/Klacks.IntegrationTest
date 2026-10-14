// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// End-to-end check of send_message and read_messages for a REAL group-restricted Supervisor against PostgreSQL (IT
/// database klacks). The skills run through the real SkillExecutorService, which sets the ambient user from the
/// execution context; behind them run the real MessengerContactRepository on the real messenger_contact table, the
/// plugin's MessagingAccessScope and the host's visibility pipeline (UserService, GroupVisibilityService,
/// ClientGroupFilterService, ClientVisibilityGuard, ClientVisibilityReaderBridge) on real groups, clients and
/// group_visibility rows. Substituted are the Identity user manager (a non-admin), the messaging service's outbound
/// side and its stored message list (the provider table holds encrypted configuration that a test must not fake), and
/// the adapters. send_message runs as it does in production: the real SendMessageConfirmationPreviewProvider resolves the
/// recipient with the real MessageRecipientResolver under the caller as ambient user (the executor sets it before its
/// autonomy gate asks the provider) and returns the binding, and the confirmed replay carries it into the executor.
/// Covers: a hidden client's contact is answered like a missing one already at the preview and nothing is sent,
/// a visible client's contact is sent to, a raw chat id of the hidden client is refused, a call without a confirmed
/// preview sends nothing, an address changed between preview and confirmation sends nothing, hidden clients that sort
/// first in the name search do not push the visible one out of it, read_messages shows only messages of visible
/// clients, an Admin context keeps the unrestricted view, and a call without any user is refused by the executor while
/// the guard behind it still answers unrestricted for such a call.
///
/// Every row carries the INTEGRATION_TEST_B8_ prefix and the cleanup deletes exactly the rows that start with it.
/// </summary>

using Klacks.Api.Application.Services.Clients;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Plugins;
using Klacks.IntegrationTest.TestHelpers;
using Klacks.Plugin.Contracts;
using Klacks.Plugin.Contracts.Skills;
using Klacks.Plugin.Messaging;
using Klacks.Plugin.Messaging.Application.Interfaces;
using Klacks.Plugin.Messaging.Application.Services;
using Klacks.Plugin.Messaging.Domain.Enums;
using Klacks.Plugin.Messaging.Domain.Interfaces;
using Klacks.Plugin.Messaging.Domain.Models;
using Klacks.Plugin.Messaging.Infrastructure.Repositories;
using Klacks.Plugin.Messaging.Skills;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using ApiSkillExecutionContext = Klacks.Api.Domain.Models.Assistant.SkillExecutionContext;
using SkillResult = Klacks.Api.Domain.Models.Assistant.SkillResult;

namespace Klacks.IntegrationTest.Messaging;

[TestFixture]
[Category("RealDatabase")]
[NonParallelizable]
public class SupervisorMessagingSkillVisibilityIntegrationTests
{
    private const string TestPrefix = "INTEGRATION_TEST_B8_";
    private const string VisibleChatId = "9100000000000001";
    private const string HiddenChatId = "9100000000000002";
    private const string ProviderName = "telegram";
    private const string VisibleContent = "INTEGRATION_TEST_B8_ visible client message";
    private const string HiddenContent = "INTEGRATION_TEST_B8_ hidden client message";
    private const string ClientlessContent = "INTEGRATION_TEST_B8_ owner bridge message";
    private const string ChangedVisibleChatId = "9100000000000009";
    private const int HiddenNameTwins = 6;

    private DataBaseContext _context = null!;
    private SupervisorVisibilityScenario _scenario = null!;
    private IMessagingService _messagingService = null!;
    private MessagingAccessScope _accessScope = null!;
    private MessageRecipientResolver _resolver = null!;
    private SendMessageConfirmationPreviewProvider _previewProvider = null!;
    private SendMessageSkill _sendMessageSkill = null!;
    private ReadMessagesSkill _readMessagesSkill = null!;
    private SkillExecutorService _executor = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await using var context = new MessagingPluginDataBaseContext(
            SupervisorVisibilityScenario.BuildOptions(), Substitute.For<IHttpContextAccessor>());
        await SupervisorVisibilityScenario.CleanupAsync(context, TestPrefix);
    }

    [SetUp]
    public async Task SetUp()
    {
        _context = new MessagingPluginDataBaseContext(
            SupervisorVisibilityScenario.BuildOptions(), Substitute.For<IHttpContextAccessor>());
        _scenario = await SupervisorVisibilityScenario.CreateAsync(TestPrefix, _context);

        await InsertContactAsync(_scenario.VisibleClient.Id, VisibleChatId);
        await InsertContactAsync(_scenario.HiddenClient.Id, HiddenChatId);

        var contactRepository = new MessengerContactRepository(_context);
        _messagingService = Substitute.For<IMessagingService>();
        _messagingService.SendMessageAsync(Arg.Any<string>(), Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SendMessageResult(true, ExternalMessageId: "ext-b8"));
        _messagingService.ResolveRecipientClientIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
                (await contactRepository.GetByTypeAndValueAsync(MessengerType.Telegram, call.ArgAt<string>(1)))?.ClientId);
        _messagingService.GetMessagesAsync(
                Arg.Any<Guid?>(), Arg.Any<MessageDirection?>(), Arg.Any<string?>(), Arg.Any<MessageScope?>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<int>(5) == 0
                ? new List<Message>
                {
                    Stored(_scenario.VisibleClient.Id, VisibleContent),
                    Stored(_scenario.HiddenClient.Id, HiddenContent),
                    Stored(null, ClientlessContent)
                }
                : new List<Message>());

        _accessScope = new MessagingAccessScope(
            _messagingService,
            new ClientVisibilityReaderBridge(_scenario.Guard),
            Substitute.For<IClientGroupReader>(),
            Substitute.For<IClientIdNumberReader>());

        _resolver = new MessageRecipientResolver(
            _messagingService,
            contactRepository,
            Substitute.For<IOwnerMessengerReader>(),
            Substitute.For<IAppUserDirectoryReader>(),
            Substitute.For<IUserMessengerContactRepository>(),
            Substitute.For<IMessagingProviderRepository>(),
            _accessScope);
        _previewProvider = new SendMessageConfirmationPreviewProvider(_resolver);
        _sendMessageSkill = new SendMessageSkill(_messagingService, _resolver);
        _readMessagesSkill = new ReadMessagesSkill(
            Substitute.For<IMessageRepository>(), Substitute.For<IMessagingProviderRepository>(), _accessScope);
        _executor = BuildExecutor();
    }

    [TearDown]
    public async Task TearDown()
    {
        await SupervisorVisibilityScenario.CleanupAsync(_context, TestPrefix);
        await _context.DisposeAsync();
    }

    [Test]
    public async Task SendMessage_ToTheHiddenClientsName_IsAnsweredLikeAMissingContact_AndNothingIsSent()
    {
        var hidden = await SendConfirmedAsync(SupervisorContext(), SendParameters(_scenario.HiddenClient.Name));
        var missing = await SendConfirmedAsync(SupervisorContext(), SendParameters(TestPrefix + "NOBODY"));

        hidden.Success.ShouldBeFalse();
        hidden.Message.ShouldContain("No Telegram contact found");
        hidden.Message!.Replace(_scenario.HiddenClient.Name, "X").ShouldBe(missing.Message!.Replace(TestPrefix + "NOBODY", "X"));
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }

    [Test]
    public async Task SendMessage_ToTheVisibleClientsName_SendsToItsChatId()
    {
        var result = await SendConfirmedAsync(SupervisorContext(), SendParameters(_scenario.VisibleClient.Name));

        result.Success.ShouldBeTrue(result.Message);
        await _messagingService.Received(1).SendMessageAsync(
            ProviderName, Arg.Is<SendMessageRequest>(r => r.Recipient == VisibleChatId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SendMessage_ToAVisibleClientWithoutAFirstName_StillResolvesByName()
    {
        await _context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE client SET first_name = NULL WHERE id = {_scenario.VisibleClient.Id}");

        var result = await SendConfirmedAsync(SupervisorContext(), SendParameters(_scenario.VisibleClient.Name));

        result.Success.ShouldBeTrue(result.Message);
        await _messagingService.Received(1).SendMessageAsync(
            ProviderName, Arg.Is<SendMessageRequest>(r => r.Recipient == VisibleChatId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SendMessage_ToTheHiddenClientsRawChatId_IsRefused_ToTheVisibleOnesRawChatId_Passes()
    {
        var hidden = await SendConfirmedAsync(SupervisorContext(), SendParameters(HiddenChatId));
        var visible = await SendConfirmedAsync(SupervisorContext(), SendParameters(VisibleChatId));

        hidden.Success.ShouldBeFalse();
        visible.Success.ShouldBeTrue(visible.Message);
        await _messagingService.Received(1).SendMessageAsync(
            ProviderName, Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>());
        await _messagingService.Received(1).SendMessageAsync(
            ProviderName, Arg.Is<SendMessageRequest>(r => r.Recipient == VisibleChatId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SendMessage_ToARawNumberWithoutAnyContact_IsRefusedForTheSupervisor()
    {
        var result = await SendConfirmedAsync(SupervisorContext(), SendParameters("+41790000000"));

        result.Success.ShouldBeFalse();
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }

    [Test]
    public async Task SendMessage_WithoutAConfirmedPreview_IsRefused_AndNothingIsSent()
    {
        var result = await ExecuteAsync("send_message", SupervisorContext(), SendParameters(_scenario.VisibleClient.Name));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(string.Format(PluginConfirmationTexts.BindingRequired, "send_message"));
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }

    [Test]
    public async Task SendMessage_WhoseAddressChangedAfterThePreview_SendsNothing()
    {
        var context = SupervisorContext();
        var parameters = SendParameters(_scenario.VisibleClient.Name);
        var preview = await PreviewAsync(context, parameters);
        preview.IsRefusal.ShouldBeFalse(preview.Text);
        preview.Text.ShouldContain(VisibleChatId);
        await _context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE messenger_contact SET value = {ChangedVisibleChatId} WHERE client_id = {_scenario.VisibleClient.Id}");

        var result = await ExecuteAsync("send_message", context with { ConfirmedActionBinding = preview.Binding }, parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(PluginConfirmationTexts.ScopeChanged);
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);
    }

    [Test]
    public async Task SendMessage_HiddenClientsThatSortFirstInTheNameSearch_DoNotPushTheVisibleOneOut()
    {
        for (var i = 0; i < HiddenNameTwins; i++)
        {
            var twin = await _scenario.CreateClientAsync($"HIDDEN_TWIN_{i}", _scenario.HiddenGroup.Id);
            await InsertContactAsync(twin.Id, $"91000000000001{i:D2}", DateTime.UtcNow.AddHours(-1).AddMinutes(i));
        }

        var result = await SendConfirmedAsync(SupervisorContext(), SendParameters(TestPrefix));

        result.Success.ShouldBeTrue(result.Message);
        await _messagingService.Received(1).SendMessageAsync(
            ProviderName, Arg.Is<SendMessageRequest>(r => r.Recipient == VisibleChatId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReadMessages_ShowsOnlyMessagesOfVisibleClients_NeverTheHiddenOnesNorThoseWithoutAClient()
    {
        var result = await ExecuteAsync("read_messages", SupervisorContext(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue(result.Message);
        ContentsOf(result.Data).ShouldBe(new[] { VisibleContent });
    }

    [Test]
    public async Task ReadMessages_AsAdmin_KeepsTheUnrestrictedView()
    {
        var repository = Substitute.For<IMessageRepository>();
        repository.GetMessagesAsync(null, null, null, null, 10, 0).Returns(new List<Message>
        {
            Stored(_scenario.VisibleClient.Id, VisibleContent),
            Stored(_scenario.HiddenClient.Id, HiddenContent),
            Stored(null, ClientlessContent)
        });
        var adminSkill = new ReadMessagesSkill(repository, Substitute.For<IMessagingProviderRepository>(), _accessScope);

        var result = await adminSkill.ExecuteAsync(
            new Klacks.Plugin.Contracts.Skills.SkillExecutionContext
            {
                UserId = Guid.Parse(_scenario.Supervisor.Id),
                TenantId = Guid.Empty,
                UserName = "admin",
                UserPermissions = new[] { Roles.Admin }
            },
            new Dictionary<string, object>());

        ContentsOf(result.Data).ShouldBe(new[] { VisibleContent, HiddenContent, ClientlessContent });
    }

    [Test]
    public async Task CallWithoutAnyUser_IsRefusedByTheExecutor_WhileTheGuardBehindItIsStillUnrestricted()
    {
        var withoutUser = SupervisorContext(Guid.Empty);

        var read = await ExecuteAsync("read_messages", withoutUser, new Dictionary<string, object>());
        var send = await ExecuteAsync("send_message", withoutUser, SendParameters(_scenario.HiddenClient.Name));

        read.Success.ShouldBeFalse();
        read.Message.ShouldBe(GroupVisibilityBoundSkills.UnidentifiedCallerMessage);
        send.Success.ShouldBeFalse();
        send.Message.ShouldBe(GroupVisibilityBoundSkills.UnidentifiedCallerMessage);
        await _messagingService.DidNotReceiveWithAnyArgs().SendMessageAsync(default!, default!, default);

        ExecutionPrincipal.CurrentUserId.ShouldBeNull();
        (await _scenario.Guard.IsVisibleAsync(_scenario.HiddenClient.Id)).ShouldBeTrue(
            "Characterization: without any user the host guard answers unrestricted, which suits background jobs. The " +
            "executor refusal above is what keeps a skill call made for a person from reaching it. A later fail-closed " +
            "guard must change this assertion on purpose.");
    }

    private SkillExecutorService BuildExecutor()
    {
        var registry = Substitute.For<ISkillRegistry>();
        registry.GetSkillByName("send_message").Returns(Descriptor("send_message", typeof(SendMessageSkill)));
        registry.GetSkillByName("read_messages").Returns(Descriptor("read_messages", typeof(ReadMessagesSkill)));

        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(SendMessageSkill)).Returns(_sendMessageSkill);
        services.GetService(typeof(ReadMessagesSkill)).Returns(_readMessagesSkill);

        var gate = Substitute.For<IAutonomyGate>();
        gate.CheckAsync(
                Arg.Any<SkillDescriptor>(), Arg.Any<ApiSkillExecutionContext>(),
                Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns((SkillResult?)null);
        var scenarioGuard = Substitute.For<ISkillScenarioScopeGuard>();
        scenarioGuard.CheckAsync(Arg.Any<Dictionary<string, object>>(), Arg.Any<CancellationToken>())
            .Returns((SkillResult?)null);

        return new SkillExecutorService(
            registry,
            Substitute.For<ISkillUsageTracker>(),
            services,
            Substitute.For<IGenericSkillDispatcher>(),
            gate,
            Substitute.For<IEntityChangeNotifier>(),
            Substitute.For<IRecentEntityRegistrar>(),
            scenarioGuard,
            Substitute.For<ILogger<SkillExecutorService>>());
    }

    private static SkillDescriptor Descriptor(string name, Type implementation) =>
        new(name, "test", SkillCategory.Action, [], [Permissions.CanEditClients], [], implementation);

    private Task<SkillResult> ExecuteAsync(string skill, ApiSkillExecutionContext context, Dictionary<string, object> parameters) =>
        _executor.ExecuteAsync(new SkillInvocation { SkillName = skill, Parameters = parameters }, context);

    private async Task<SkillConfirmationPreview> PreviewAsync(
        ApiSkillExecutionContext context, Dictionary<string, object> parameters)
    {
        using var principal = ExecutionPrincipal.Begin(context.UserId);
        return await _previewProvider.BuildAsync("send_message", context, parameters);
    }

    private async Task<SkillResult> SendConfirmedAsync(ApiSkillExecutionContext context, Dictionary<string, object> parameters)
    {
        var preview = await PreviewAsync(context, parameters);
        if (preview.IsRefusal)
        {
            return SkillResult.Error(preview.Text);
        }

        return await ExecuteAsync("send_message", context with { ConfirmedActionBinding = preview.Binding }, parameters);
    }

    private ApiSkillExecutionContext SupervisorContext(Guid? userId = null) => new()
    {
        UserId = userId ?? Guid.Parse(_scenario.Supervisor.Id),
        TenantId = Guid.Empty,
        UserName = "Klacksy telegram",
        UserPermissions = Permissions.ExpandRoles(new[] { Roles.Authorised }),
        BypassAutonomyGate = true
    };

    private static Dictionary<string, object> SendParameters(string recipient) => new()
    {
        ["provider"] = ProviderName,
        ["recipient"] = recipient,
        ["content"] = "INTEGRATION_TEST_B8_ hello"
    };

    private static Message Stored(Guid? clientId, string content) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        Sender = "9100000000000099",
        Recipient = "bot",
        Content = content,
        Direction = MessageDirection.Inbound,
        Status = MessageStatus.Delivered,
        Timestamp = DateTime.UtcNow
    };

    private static IReadOnlyList<string> ContentsOf(object? data) =>
        ((System.Collections.IEnumerable)data!).Cast<object>()
            .Select(row => (string)row.GetType().GetProperty(nameof(Message.Content))!.GetValue(row)!)
            .ToList();

    private async Task InsertContactAsync(Guid clientId, string chatId, DateTime? createTime = null)
    {
        await _context.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO messenger_contact (id, client_id, type, value, description, is_deleted, create_time)
            VALUES ({Guid.NewGuid()}, {clientId}, {(int)MessengerType.Telegram}, {chatId}, {TestPrefix}, false, {createTime ?? DateTime.UtcNow})");
    }

    private sealed class MessagingPluginDataBaseContext : DataBaseContext
    {
        public MessagingPluginDataBaseContext(
            DbContextOptions<DataBaseContext> options, IHttpContextAccessor httpContextAccessor)
            : base(options, httpContextAccessor)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            new MessagingPluginRegistrar().ConfigureDbModel(modelBuilder);
        }
    }
}
