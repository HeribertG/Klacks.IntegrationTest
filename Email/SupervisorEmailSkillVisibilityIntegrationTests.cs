// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// End-to-end check of the inbox skills (read_email, list_emails, mark_email_read, list_emails_by_group,
/// list_emails_by_client, move_email_to_folder, delete_email, restore_email, get_email_group_tree) for a REAL
/// group-restricted Supervisor against PostgreSQL (IT database klacks). The skills run through the real
/// SkillExecutorService, which sets the ambient user from the execution context; behind them run the real handlers
/// (GetReceivedEmailQueryHandler, GetReceivedEmailsQueryHandler, MarkEmailAsReadCommandHandler,
/// GetEmailsByClientQueryHandler, MoveEmailToFolderCommandHandler, DeleteReceivedEmailCommandHandler,
/// RestoreEmailCommandHandler, GetEmailGroupTreeQueryHandler), the real ReceivedEmailRepository, EmailQueryRepository,
/// EmailFolderRepository, ClientSearchRepository, ClientRepository and GroupHierarchyService, the real UnitOfWork and
/// the host's visibility pipeline on real groups, clients, communications, e-mail folders and group_visibility rows.
/// The unit tests of those handlers substitute the guard; this fixture proves the skill path. A mail has no client id:
/// it belongs to the clients whose address equals the sender address, and is hidden when one of them is outside the
/// caller's groups. Covers: the mail of the hidden client is answered like a missing mail, listed counts leave it out,
/// marking it read, moving it, trashing it and restoring it from the trash are refused and leave the row unchanged, a
/// hidden group cannot be resolved, a hidden client is answered like an unknown client and its mail stays out of a
/// visible client's list even over a shared address, the group tree leaves out the hidden group, the hidden client and
/// their counters, mails of a visible client and of an unknown sender stay reachable, and a call without any user is
/// refused by the executor. The move, delete and restore skills refuse the hidden mail at their first read and
/// list_emails_by_client at the group-scoped name search, so on that path the command and query handlers' own guards
/// are not reached. Substituted: the Identity user manager (a non-admin), the IMAP and notification services, the group
/// repository, the company clock's settings (UTC) and the mediator's routing.
///
/// Every row carries the INTEGRATION_TEST_B8MAIL_ prefix and the cleanup deletes exactly the rows that start with it.
/// Mails filed for clients sit in the shared client-assigned folder and trashed mails in the installation's trash
/// folder; both are column values of prefixed rows. Only the move target and, on a database without one, the trash
/// folder are created as e-mail folders, both prefixed.
/// </summary>

using Klacks.Api.Application.Handlers.Email;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.Email;
using Klacks.Api.Application.Commands.Email;
using Klacks.Api.Application.DTOs.Email;
using Klacks.Api.Application.Services.Grouping;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Email;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Email;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.Api.Domain.Services.Clients;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Email;
using Klacks.Api.Infrastructure.Repositories.Staffs;
using Klacks.Api.Infrastructure.Scripting;
using Klacks.Api.Infrastructure.Services;
using Klacks.Api.Infrastructure.Services.Clients;
using Klacks.Api.Infrastructure.Services.Groups;
using Klacks.IntegrationTest.TestHelpers;
using System.Collections;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Group = Klacks.Api.Domain.Models.Associations.Group;
using IEmailNotificationService = Klacks.Api.Domain.Interfaces.Email.IEmailNotificationService;

namespace Klacks.IntegrationTest.Email;

[TestFixture]
[Category("RealDatabase")]
[NonParallelizable]
public class SupervisorEmailSkillVisibilityIntegrationTests
{
    private const string TestPrefix = "INTEGRATION_TEST_B8MAIL_";
    private const string Folder = TestPrefix + "INBOX";
    private const string ArchiveFolder = TestPrefix + "ARCHIVE";
    private const string TestTrashFolder = TestPrefix + "TRASH";
    private const int TestFolderSortOrder = 10_000;
    private const string VisibleAddress = "integration_test_b8mail_visible@example.invalid";
    private const string HiddenAddress = "integration_test_b8mail_hidden@example.invalid";
    private const string UnknownAddress = "integration_test_b8mail_unknown@example.invalid";

    private const string ReadEmail = "read_email";
    private const string ListEmails = "list_emails";
    private const string MarkEmailRead = "mark_email_read";
    private const string ListEmailsByGroup = "list_emails_by_group";
    private const string ListEmailsByClient = "list_emails_by_client";
    private const string MoveEmailToFolder = "move_email_to_folder";
    private const string DeleteEmail = "delete_email";
    private const string RestoreEmail = "restore_email";
    private const string GetEmailGroupTree = "get_email_group_tree";

    private const string EmailIdParameter = "emailId";
    private const string FolderParameter = "folder";
    private const string GroupNameParameter = "groupName";
    private const string FirstNameParameter = "firstName";
    private const string LastNameParameter = "lastName";
    private const string EmailsProperty = "Emails";
    private const string TreeProperty = "Tree";

    private DataBaseContext _context = null!;
    private SupervisorVisibilityScenario _scenario = null!;
    private EmailFolderRepository _folders = null!;
    private RecordingMediator _mediator = null!;
    private SkillExecutorService _executor = null!;
    private ReceivedEmail _fromVisible = null!;
    private ReceivedEmail _fromHidden = null!;
    private ReceivedEmail _fromUnknown = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await using var context = NewContext();
        await SupervisorVisibilityScenario.CleanupAsync(context, TestPrefix);
    }

    [SetUp]
    public async Task SetUp()
    {
        _context = NewContext();
        _scenario = await SupervisorVisibilityScenario.CreateAsync(TestPrefix, _context);
        _folders = new EmailFolderRepository(_context);

        _context.Set<Communication>().AddRange(
            NewMailAddress(_scenario.VisibleClient.Id, VisibleAddress),
            NewMailAddress(_scenario.HiddenClient.Id, HiddenAddress));
        _fromVisible = NewEmail("VISIBLE", VisibleAddress);
        _fromHidden = NewEmail("HIDDEN", HiddenAddress);
        _fromUnknown = NewEmail("UNKNOWN", UnknownAddress);
        _context.ReceivedEmails.AddRange(_fromVisible, _fromHidden, _fromUnknown);
        await _context.SaveChangesAsync();

        _mediator = BuildMediator();
        _executor = BuildExecutor();
    }

    [TearDown]
    public async Task TearDown()
    {
        await SupervisorVisibilityScenario.CleanupAsync(_context, TestPrefix);
        await _context.DisposeAsync();
    }

    [Test]
    public async Task ReadEmail_OfTheHiddenClientsMail_IsAnsweredLikeAMissingMail_VisibleAndUnknownSenderAreReadable()
    {
        var hidden = await ExecuteAsync(ReadEmail, SupervisorContext(), EmailParameters(_fromHidden));
        var missing = await ExecuteAsync(ReadEmail, SupervisorContext(), EmailParameters(Guid.NewGuid()));
        var visible = await ExecuteAsync(ReadEmail, SupervisorContext(), EmailParameters(_fromVisible));
        var unknown = await ExecuteAsync(ReadEmail, SupervisorContext(), EmailParameters(_fromUnknown));

        hidden.Success.ShouldBeFalse();
        WithoutGuids(hidden.Message).ShouldBe(WithoutGuids(missing.Message));
        visible.Success.ShouldBeTrue(visible.Message);
        unknown.Success.ShouldBeTrue(unknown.Message);
    }

    [Test]
    public async Task ListEmails_LeavesOutTheHiddenClientsMail_AndCountsOnlyWhatIsVisible()
    {
        var result = await ExecuteAsync(ListEmails, SupervisorContext(), new Dictionary<string, object> { [FolderParameter] = Folder });

        result.Success.ShouldBeTrue(result.Message);
        var subjects = ProjectedValues(result.Data, nameof(ReceivedEmail.Subject));
        subjects.ShouldBe(new[] { _fromVisible.Subject, _fromUnknown.Subject }, ignoreOrder: true);
        Property(result.Data!, nameof(ReceivedEmailListResponse.TotalCount)).ShouldBe(2);
        result.Message.ShouldNotContain(_fromHidden.Subject);
    }

    [Test]
    public async Task MarkEmailRead_OfTheHiddenClientsMail_IsRefused_AndTheRowStaysUnread()
    {
        var hidden = await ExecuteAsync(MarkEmailRead, SupervisorContext(), EmailParameters(_fromHidden));
        var visible = await ExecuteAsync(MarkEmailRead, SupervisorContext(), EmailParameters(_fromVisible));

        hidden.Success.ShouldBeFalse();
        visible.Success.ShouldBeTrue(visible.Message);
        (await IsReadInDatabaseAsync(_fromHidden.Id)).ShouldBeFalse();
        (await IsReadInDatabaseAsync(_fromVisible.Id)).ShouldBeTrue();
    }

    [Test]
    public async Task ListEmailsByGroup_OfTheHiddenGroup_CannotBeResolved_AndNoQueryIsSent()
    {
        var result = await ExecuteAsync(
            ListEmailsByGroup,
            SupervisorContext(),
            new Dictionary<string, object> { [GroupNameParameter] = _scenario.HiddenGroup.Name });

        result.Success.ShouldBeFalse();
        _mediator.Sent.ShouldBeEmpty();
    }

    [Test]
    public async Task ListEmailsByClient_OfTheHiddenClient_IsAnsweredLikeAnUnknownClient_TheVisibleClientsMailIsListed()
    {
        var (visibleMail, _) = await AddClientAssignedEmailsAsync();

        var hidden = await ExecuteAsync(ListEmailsByClient, SupervisorContext(), ClientParameters(_scenario.HiddenClient));
        var sentForHidden = _mediator.Sent.ToList();
        var visible = await ExecuteAsync(ListEmailsByClient, SupervisorContext(), ClientParameters(_scenario.VisibleClient));

        hidden.Success.ShouldBeFalse();
        hidden.Message.ShouldBe(ClientResolver.NotFoundMessage(_scenario.HiddenClient.FirstName, _scenario.HiddenClient.Name));
        sentForHidden.ShouldBeEmpty();
        visible.Success.ShouldBeTrue(visible.Message);
        ProjectedValues(visible.Data, nameof(ReceivedEmail.Subject)).ShouldBe(new[] { visibleMail.Subject });
        Property(visible.Data!, nameof(ReceivedEmailListResponse.TotalCount)).ShouldBe(1);
    }

    [Test]
    public async Task ListEmailsByClient_OfTheVisibleClient_LeavesOutMailFromAnAddressItSharesWithTheHiddenClient()
    {
        _context.Set<Communication>().Add(NewMailAddress(_scenario.VisibleClient.Id, HiddenAddress));
        await _context.SaveChangesAsync();
        var (visibleMail, hiddenMail) = await AddClientAssignedEmailsAsync();

        var result = await ExecuteAsync(ListEmailsByClient, SupervisorContext(), ClientParameters(_scenario.VisibleClient));

        result.Success.ShouldBeTrue(result.Message);
        var subjects = ProjectedValues(result.Data, nameof(ReceivedEmail.Subject));
        subjects.ShouldBe(new[] { visibleMail.Subject });
        subjects.ShouldNotContain(hiddenMail.Subject);
        Property(result.Data!, nameof(ReceivedEmailListResponse.TotalCount)).ShouldBe(1);
    }

    [Test]
    public async Task MoveEmailToFolder_OfTheHiddenClientsMail_IsRefusedLikeAMissingMail_AndTheRowStaysInItsFolder()
    {
        await AddFolderAsync(ArchiveFolder, specialUse: null);

        var hidden = await ExecuteAsync(MoveEmailToFolder, SupervisorContext(), MoveParameters(_fromHidden.Id, ArchiveFolder));
        var missing = await ExecuteAsync(MoveEmailToFolder, SupervisorContext(), MoveParameters(Guid.NewGuid(), ArchiveFolder));
        var sentBeforeVisible = _mediator.Sent.ToList();
        var visible = await ExecuteAsync(MoveEmailToFolder, SupervisorContext(), MoveParameters(_fromVisible.Id, ArchiveFolder));

        hidden.Success.ShouldBeFalse();
        WithoutGuids(hidden.Message).ShouldBe(WithoutGuids(missing.Message));
        sentBeforeVisible.ShouldNotContain(typeof(MoveEmailToFolderCommand));
        visible.Success.ShouldBeTrue(visible.Message);
        (await FolderInDatabaseAsync(_fromHidden.Id)).ShouldBe(Folder);
        (await FolderInDatabaseAsync(_fromVisible.Id)).ShouldBe(ArchiveFolder);
    }

    [Test]
    public async Task DeleteEmail_OfTheHiddenClientsMail_IsRefusedLikeAMissingMail_AndTheRowStaysOutOfTheTrash()
    {
        var trash = await TrashFolderAsync();

        var hidden = await ExecuteAsync(DeleteEmail, SupervisorContext(), EmailParameters(_fromHidden));
        var missing = await ExecuteAsync(DeleteEmail, SupervisorContext(), EmailParameters(Guid.NewGuid()));
        var sentBeforeVisible = _mediator.Sent.ToList();
        var visible = await ExecuteAsync(DeleteEmail, SupervisorContext(), EmailParameters(_fromVisible));

        hidden.Success.ShouldBeFalse();
        WithoutGuids(hidden.Message).ShouldBe(WithoutGuids(missing.Message));
        sentBeforeVisible.ShouldNotContain(typeof(DeleteReceivedEmailCommand));
        visible.Success.ShouldBeTrue(visible.Message);
        (await FolderInDatabaseAsync(_fromHidden.Id)).ShouldBe(Folder);
        (await FolderInDatabaseAsync(_fromVisible.Id)).ShouldBe(trash);
    }

    [Test]
    public async Task RestoreEmail_OfTheHiddenClientsMailInTheTrash_IsRefusedLikeAMissingMail_AndTheRowStaysInTheTrash()
    {
        var trash = await TrashFolderAsync();
        _fromHidden.Folder = trash;
        _fromVisible.Folder = trash;
        await _context.SaveChangesAsync();

        var hidden = await ExecuteAsync(RestoreEmail, SupervisorContext(), EmailParameters(_fromHidden));
        var missing = await ExecuteAsync(RestoreEmail, SupervisorContext(), EmailParameters(Guid.NewGuid()));
        var sentBeforeVisible = _mediator.Sent.ToList();
        var visible = await ExecuteAsync(RestoreEmail, SupervisorContext(), EmailParameters(_fromVisible));

        hidden.Success.ShouldBeFalse();
        WithoutGuids(hidden.Message).ShouldBe(WithoutGuids(missing.Message));
        sentBeforeVisible.ShouldNotContain(typeof(RestoreEmailCommand));
        visible.Success.ShouldBeTrue(visible.Message);
        (await FolderInDatabaseAsync(_fromHidden.Id)).ShouldBe(trash);
        (await FolderInDatabaseAsync(_fromVisible.Id)).ShouldNotBe(trash);
    }

    [Test]
    public async Task GetEmailGroupTree_LeavesOutTheHiddenGroupAndClient_AndCountsOnlyTheVisibleClientsMail()
    {
        await AddClientAssignedEmailsAsync();

        var result = await ExecuteAsync(GetEmailGroupTree, SupervisorContext(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue(result.Message);
        var nodes = TreeNodes(result.Data);
        var nodeIds = nodes.Select(node => node.Id).ToList();
        nodeIds.ShouldNotContain(_scenario.HiddenGroup.Id);
        nodeIds.ShouldNotContain(_scenario.HiddenClient.Id);
        var visibleGroup = nodes.Single(node => node.Id == _scenario.VisibleGroup.Id);
        var visibleClient = nodes.Single(node => node.Id == _scenario.VisibleClient.Id);
        visibleGroup.EmailCount.ShouldBe(1);
        visibleGroup.UnreadCount.ShouldBe(1);
        visibleClient.EmailCount.ShouldBe(1);
        visibleClient.UnreadCount.ShouldBe(1);
    }

    [Test]
    public async Task CallWithoutAnyUser_IsRefusedByTheExecutor_BeforeAnyHandlerRuns()
    {
        var result = await ExecuteAsync(ReadEmail, SupervisorContext(Guid.Empty), EmailParameters(_fromHidden));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(GroupVisibilityBoundSkills.UnidentifiedCallerMessage);
        _mediator.Sent.ShouldBeEmpty();
    }

    private static DataBaseContext NewContext() =>
        new(SupervisorVisibilityScenario.BuildOptions(), Substitute.For<IHttpContextAccessor>());

    private RecordingMediator BuildMediator()
    {
        var repository = new ReceivedEmailRepository(_context, Substitute.For<ILogger<ReceivedEmailRepository>>());
        var queries = new EmailQueryRepository(_context);
        var mapper = new ReceivedEmailMapper();
        var unitOfWork = new UnitOfWork(_context, Substitute.For<ILogger<UnitOfWork>>());
        var imap = Substitute.For<IImapEmailService>();
        var hierarchy = new GroupHierarchyService(
            _context, Substitute.For<ILogger<GroupHierarchyService>>(), _scenario.GroupVisibility, TestCompanyClock.Utc());

        return new RecordingMediator()
            .Route(new GetReceivedEmailQueryHandler(
                repository, queries, _scenario.Guard, mapper, Substitute.For<ILogger<GetReceivedEmailQueryHandler>>()))
            .Route(new GetReceivedEmailsQueryHandler(
                repository, queries, _scenario.Guard, mapper, Substitute.For<ILogger<GetReceivedEmailsQueryHandler>>()))
            .Route(new MarkEmailAsReadCommandHandler(
                repository,
                queries,
                _scenario.Guard,
                unitOfWork,
                Substitute.For<IEmailNotificationService>(),
                imap,
                Substitute.For<ILogger<MarkEmailAsReadCommandHandler>>()))
            .Route(new GetEmailsByClientQueryHandler(
                queries, _scenario.Guard, mapper, Substitute.For<ILogger<GetEmailsByClientQueryHandler>>()))
            .Route(new MoveEmailToFolderCommandHandler(
                repository, queries, _scenario.Guard, unitOfWork, imap, Substitute.For<ILogger<MoveEmailToFolderCommandHandler>>()))
            .Route(new DeleteReceivedEmailCommandHandler(
                repository,
                queries,
                _scenario.Guard,
                _folders,
                unitOfWork,
                imap,
                Substitute.For<ILogger<DeleteReceivedEmailCommandHandler>>()))
            .Route(new RestoreEmailCommandHandler(
                repository,
                queries,
                _scenario.Guard,
                _folders,
                unitOfWork,
                imap,
                Substitute.For<ILogger<RestoreEmailCommandHandler>>()))
            .Route(new GetEmailGroupTreeQueryHandler(
                hierarchy, queries, _scenario.Guard, Substitute.For<ILogger<GetEmailGroupTreeQueryHandler>>()));
    }

    private SkillExecutorService BuildExecutor()
    {
        var groupRepository = Substitute.For<IGroupRepository>();
        var groups = new List<Group> { _scenario.VisibleGroup, _scenario.HiddenGroup };
        groupRepository.List().Returns(groups);
        groupRepository.GetRoots().Returns(groups);
        var groupScopeGuard = new GroupScopeGuard(_scenario.GroupVisibility, groupRepository);

        var skills = new Dictionary<string, object>
        {
            [ReadEmail] = new ReadEmailSkill(_mediator),
            [ListEmails] = new ListEmailsSkill(_mediator),
            [MarkEmailRead] = new MarkEmailReadSkill(_mediator),
            [ListEmailsByGroup] = new ListEmailsByGroupSkill(groupRepository, groupScopeGuard, _mediator),
            [ListEmailsByClient] = new ListEmailsByClientSkill(_scenario.ClientSearch, BuildClientRepository(), _mediator),
            [MoveEmailToFolder] = new MoveEmailToFolderSkill(_mediator, _folders),
            [DeleteEmail] = new DeleteEmailSkill(_mediator, _folders),
            [RestoreEmail] = new RestoreEmailSkill(_mediator, _folders),
            [GetEmailGroupTree] = new GetEmailGroupTreeSkill(_mediator)
        };
        var registry = Substitute.For<ISkillRegistry>();
        foreach (var (name, skill) in skills)
        {
            registry.GetSkillByName(name).Returns(Descriptor(name, skill.GetType()));
        }

        var instances = skills.Values.ToDictionary(skill => skill.GetType());
        var services = Substitute.For<IServiceProvider>();
        services.GetService(Arg.Any<Type>()).Returns(call => instances.GetValueOrDefault(call.Arg<Type>()));

        var gate = Substitute.For<IAutonomyGate>();
        gate.CheckAsync(
                Arg.Any<SkillDescriptor>(), Arg.Any<SkillExecutionContext>(),
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

    private ClientRepository BuildClientRepository()
    {
        var validator = new ClientValidator();
        return new ClientRepository(
            _context,
            new MacroEngine(),
            new ClientChangeTrackingService(_context, new ClientSortingService()),
            new ClientEntityManagementService(validator),
            new EntityCollectionUpdateService(_context),
            validator,
            Substitute.For<ILogger<ClientRepository>>());
    }

    private static SkillDescriptor Descriptor(string name, Type implementation) =>
        new(name, "test", SkillCategory.Crud, [], [Permissions.CanEditClients], [], implementation);

    private Task<SkillResult> ExecuteAsync(string skill, SkillExecutionContext context, Dictionary<string, object> parameters) =>
        _executor.ExecuteAsync(new SkillInvocation { SkillName = skill, Parameters = parameters }, context);

    private SkillExecutionContext SupervisorContext(Guid? userId = null) => new()
    {
        UserId = userId ?? Guid.Parse(_scenario.Supervisor.Id),
        TenantId = Guid.Empty,
        UserName = "Klacksy chat",
        UserPermissions = Permissions.ExpandRoles(new[] { Roles.Authorised }),
        BypassAutonomyGate = true
    };

    private static Dictionary<string, object> EmailParameters(ReceivedEmail email) => EmailParameters(email.Id);

    private static Dictionary<string, object> EmailParameters(Guid emailId) => new() { [EmailIdParameter] = emailId.ToString() };

    private static Dictionary<string, object> MoveParameters(Guid emailId, string folder) => new()
    {
        [EmailIdParameter] = emailId.ToString(),
        [FolderParameter] = folder
    };

    private static Dictionary<string, object> ClientParameters(Client client) => new()
    {
        [FirstNameParameter] = client.FirstName ?? string.Empty,
        [LastNameParameter] = client.Name
    };

    private static ReceivedEmail NewEmail(string suffix, string fromAddress, string folder = Folder) => new()
    {
        Id = Guid.NewGuid(),
        MessageId = $"{TestPrefix}{suffix}_{Guid.NewGuid():N}",
        ImapUid = Random.Shared.NextInt64(1_000_000, 9_000_000),
        Folder = folder,
        SourceImapFolder = Folder,
        FromAddress = fromAddress,
        ToAddress = "planning@example.invalid",
        Subject = $"{TestPrefix}{suffix} subject",
        BodyText = $"{TestPrefix}{suffix} body",
        ReceivedDate = DateTime.UtcNow,
        IsRead = false
    };

    private static Communication NewMailAddress(Guid clientId, string address) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        Type = CommunicationTypeEnum.PrivateMail,
        Value = address,
        Prefix = string.Empty,
        Description = TestPrefix
    };

    private async Task<(ReceivedEmail Visible, ReceivedEmail Hidden)> AddClientAssignedEmailsAsync()
    {
        var visible = NewEmail("VISIBLE_ASSIGNED", VisibleAddress, EmailConstants.ClientAssignedFolder);
        var hidden = NewEmail("HIDDEN_ASSIGNED", HiddenAddress, EmailConstants.ClientAssignedFolder);
        _context.ReceivedEmails.AddRange(visible, hidden);
        await _context.SaveChangesAsync();
        return (visible, hidden);
    }

    private async Task AddFolderAsync(string imapFolderName, string? specialUse)
    {
        _context.EmailFolders.Add(new EmailFolder
        {
            Id = Guid.NewGuid(),
            Name = imapFolderName,
            ImapFolderName = imapFolderName,
            SortOrder = TestFolderSortOrder,
            SpecialUse = specialUse
        });
        await _context.SaveChangesAsync();
    }

    private async Task<string> TrashFolderAsync()
    {
        var existing = await _folders.GetImapNameBySpecialUseAsync(FolderSpecialUse.Trash);
        if (!string.IsNullOrEmpty(existing))
        {
            return existing;
        }

        await AddFolderAsync(TestTrashFolder, FolderSpecialUse.Trash);
        return TestTrashFolder;
    }

    private static string WithoutGuids(string? message) =>
        Regex.Replace(message ?? string.Empty, "[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}", "X");

    private static object Property(object source, string name) => source.GetType().GetProperty(name)!.GetValue(source)!;

    private static IReadOnlyList<string> ProjectedValues(object? data, string property)
    {
        var emails = data!.GetType().GetProperty(EmailsProperty)?.GetValue(data) ?? data;
        return ((IEnumerable)emails).Cast<object>()
            .Select(row => (string)Property(row, property))
            .ToList();
    }

    private static List<(Guid Id, int EmailCount, int UnreadCount)> TreeNodes(object? data)
    {
        var nodes = new List<(Guid Id, int EmailCount, int UnreadCount)>();
        Collect((IEnumerable)Property(data!, TreeProperty));
        return nodes;

        void Collect(IEnumerable level)
        {
            foreach (var node in level.Cast<object>())
            {
                nodes.Add((
                    (Guid)Property(node, nameof(EmailGroupTreeNode.Id)),
                    (int)Property(node, nameof(EmailGroupTreeNode.EmailCount)),
                    (int)Property(node, nameof(EmailGroupTreeNode.UnreadCount))));
                Collect((IEnumerable)Property(node, nameof(EmailGroupTreeNode.Children)));
            }
        }
    }

    private async Task<bool> IsReadInDatabaseAsync(Guid emailId)
    {
        await using var fresh = NewContext();
        return await fresh.ReceivedEmails.AsNoTracking().Where(e => e.Id == emailId).Select(e => e.IsRead).SingleAsync();
    }

    private async Task<string> FolderInDatabaseAsync(Guid emailId)
    {
        await using var fresh = NewContext();
        return await fresh.ReceivedEmails.AsNoTracking().Where(e => e.Id == emailId).Select(e => e.Folder).SingleAsync();
    }

    private sealed class RecordingMediator : IMediator
    {
        private readonly Dictionary<Type, Func<object, CancellationToken, Task<object?>>> _routes = new();

        public List<Type> Sent { get; } = [];

        public RecordingMediator Route<TRequest, TResponse>(IRequestHandler<TRequest, TResponse> handler)
            where TRequest : IRequest<TResponse>
        {
            _routes[typeof(TRequest)] = async (request, cancellationToken) =>
                await handler.Handle((TRequest)request, cancellationToken);
            return this;
        }

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request.GetType());
            if (!_routes.TryGetValue(request.GetType(), out var route))
            {
                throw new NotSupportedException(request.GetType().Name);
            }

            return (TResponse)(await route(request, cancellationToken))!;
        }
    }
}
