// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Integration tests for EmailPollingBackgroundService.ProcessBatchAsync against the real PostgreSQL
/// schema and a real DI container (real DataBaseContext, UnitOfWork and ReceivedEmailRepository; only the
/// IMAP, spam, settings and client-assignment services are substitutes). Mail 1 runs into a real unique
/// violation on the received_emails message_id index (its message id is changed to one that another row
/// owns, so the failing UPDATE is produced by PostgreSQL, not by a mock) and must stay unprocessed and
/// unchanged, while mail 2 in its own scope still commits its folder move and ProcessedAt. Afterwards the
/// repository sequence of ImapEmailService.SyncFolderStatesAsync (tracked GetByIdAsync, change IsRead,
/// Update(), commit) run in a fresh scope, and the fetching scope's own CompleteAsync as in ExecuteAsync,
/// must not overwrite the committed Folder/ProcessedAt; a control shows that the same sync on the stale
/// fetching scope would. The IMAP half of the state sync is replaced by those repository calls (the real
/// SyncEmailStatesAsync does nothing without IMAP settings). The mails handed to ProcessBatchAsync are
/// loaded by their own ids, never via GetUnprocessedAsync, so no row outside this fixture is touched.
/// Also pins the pre-existing fact that the unique message_id index has no is_deleted filter. Rows are
/// scoped by the INTEGRATION_TEST_ prefix on MessageId.
/// </summary>

using AppSettings = Klacks.Api.Application.Constants.Settings;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Email;
using Klacks.Api.Domain.Models.Email;
using Klacks.Api.Infrastructure.Email;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Email;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Inbound;

[TestFixture]
[Category("RealDatabase")]
public class EmailPollingBatchPersistenceTests
{
    private const string TestPrefix = "INTEGRATION_TEST_BATCH_";
    private const string DefaultConnectionString = "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
    private const string InboxFolder = TestPrefix + "INBOX";
    private const string JunkFolder = TestPrefix + "JUNK";
    private const string LogMethodName = "Log";
    private const int LogLevelArgumentIndex = 0;
    private const int LogExceptionArgumentIndex = 3;
    private const string UniqueViolationSqlState = "23505";

    private string _connectionString = null!;
    private ServiceProvider _provider = null!;
    private ILogger<EmailPollingBackgroundService> _logger = null!;
    private EmailPollingBackgroundService _service = null!;
    private Guid _mail1Id;
    private Guid _mail2Id;
    private string _mail1MessageId = null!;
    private string _conflictMessageId = null!;

    [SetUp]
    public async Task SetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL") ?? DefaultConnectionString;
        await CleanupAsync();

        _mail1Id = Guid.NewGuid();
        _mail2Id = Guid.NewGuid();
        _mail1MessageId = NewMessageId();
        _conflictMessageId = NewMessageId();

        await using (var context = NewContext())
        {
            context.ReceivedEmails.Add(NewMail(_mail1Id, _mail1MessageId, imapUid: 1));
            context.ReceivedEmails.Add(NewMail(_mail2Id, NewMessageId(), imapUid: 2));
            context.ReceivedEmails.Add(NewMail(Guid.NewGuid(), _conflictMessageId, imapUid: 3));
            await context.SaveChangesAsync();
        }

        var spamFilter = Substitute.For<ISpamFilterService>();
        spamFilter.ClassifyAsync(Arg.Any<ReceivedEmail>(), Arg.Any<CancellationToken>())
            .Returns(new SpamFilterResult { IsSpam = false });

        var settings = Substitute.For<ISettingsRepository>();
        settings.GetSetting(AppSettings.EMAIL_ANALYSIS_ENABLED)
            .Returns(new Klacks.Api.Domain.Models.Settings.Settings { Value = bool.FalseString });

        var assignment = Substitute.For<IEmailClientAssignmentService>();
        assignment.When(a => a.AssignNewEmailAsync(Arg.Any<ReceivedEmail>())).Do(call =>
        {
            var email = call.Arg<ReceivedEmail>();
            if (email.Id == _mail1Id)
            {
                email.MessageId = _conflictMessageId;
            }
            else
            {
                email.Folder = EmailConstants.ClientAssignedFolder;
            }
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IHttpContextAccessor>());
        services.AddDbContext<DataBaseContext>(options => options
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention());
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IReceivedEmailRepository, ReceivedEmailRepository>();
        services.AddSingleton(spamFilter);
        services.AddSingleton(Substitute.For<IImapEmailService>());
        services.AddSingleton(settings);
        services.AddSingleton(assignment);
        _provider = services.BuildServiceProvider();

        _logger = Substitute.For<ILogger<EmailPollingBackgroundService>>();
        _service = new EmailPollingBackgroundService(_provider.GetRequiredService<IServiceScopeFactory>(), _logger);
    }

    [TearDown]
    public async Task TearDown()
    {
        _service.Dispose();
        await _provider.DisposeAsync();
        await CleanupAsync();
    }

    private DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    private async Task CleanupAsync()
    {
        await using var context = NewContext();
        await context.ReceivedEmails.IgnoreQueryFilters()
            .Where(e => e.MessageId.StartsWith(TestPrefix))
            .ExecuteDeleteAsync();
    }

    private static string NewMessageId() => TestPrefix + Guid.NewGuid().ToString("N") + "@example.com";

    private static ReceivedEmail NewMail(Guid id, string messageId, long imapUid) => new()
    {
        Id = id,
        MessageId = messageId,
        ImapUid = imapUid,
        Folder = InboxFolder,
        SourceImapFolder = InboxFolder,
        FromAddress = "anna@example.com",
        ToAddress = "klacks@example.com",
        Subject = "Integration test",
        ReceivedDate = DateTime.UtcNow
    };

    private async Task<ReceivedEmail> ReadAsync(Guid id)
    {
        await using var context = NewContext();
        return await context.ReceivedEmails.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == id);
    }

    private async Task<(IServiceScope Scope, List<ReceivedEmail> Fetched)> FetchTrackedAsync()
    {
        var scope = _provider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IReceivedEmailRepository>();
        var fetched = new List<ReceivedEmail>
        {
            (await repository.GetByIdAsync(_mail1Id))!,
            (await repository.GetByIdAsync(_mail2Id))!
        };
        return (scope, fetched);
    }

    private async Task RunBatchAsync(List<ReceivedEmail> fetched)
    {
        await _service.ProcessBatchAsync(fetched, InboxFolder, JunkFolder, CancellationToken.None);
    }

    private static async Task SyncReadStateAsync(IServiceScope scope, Guid id)
    {
        var repository = scope.ServiceProvider.GetRequiredService<IReceivedEmailRepository>();
        var tracked = await repository.GetByIdAsync(id);
        tracked!.IsRead = true;
        await repository.UpdateAsync(tracked);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CompleteAsync();
    }

    private Exception? LoggedWarningException()
    {
        return _logger.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == LogMethodName)
            .Select(call => call.GetArguments())
            .Where(arguments => arguments[LogLevelArgumentIndex] is LogLevel.Warning)
            .Select(arguments => arguments[LogExceptionArgumentIndex] as Exception)
            .FirstOrDefault(exception => exception != null);
    }

    [Test]
    public async Task UniqueViolationOnMail1_LeavesMail1Untouched_WhileMail2Commits()
    {
        var (fetchingScope, fetched) = await FetchTrackedAsync();
        using (fetchingScope)
        {
            await RunBatchAsync(fetched);
        }

        var failure = LoggedWarningException();
        failure.ShouldBeOfType<DatabaseUpdateException>();
        ((DatabaseUpdateException)failure!).IsDuplicate.ShouldBeTrue();

        var mail1 = await ReadAsync(_mail1Id);
        mail1.ProcessedAt.ShouldBeNull();
        mail1.MessageId.ShouldBe(_mail1MessageId);
        mail1.Folder.ShouldBe(InboxFolder);

        var mail2 = await ReadAsync(_mail2Id);
        mail2.ProcessedAt.ShouldNotBeNull();
        mail2.Folder.ShouldBe(EmailConstants.ClientAssignedFolder);
    }

    [Test]
    public async Task StateSyncAfterTheBatch_DoesNotOverwriteTheCommittedFolderAndProcessedAt()
    {
        var (fetchingScope, fetched) = await FetchTrackedAsync();
        using (fetchingScope)
        {
            await RunBatchAsync(fetched);

            using (var syncScope = _provider.CreateScope())
            {
                await SyncReadStateAsync(syncScope, _mail2Id);
            }

            await fetchingScope.ServiceProvider.GetRequiredService<IUnitOfWork>().CompleteAsync();
        }

        var mail2 = await ReadAsync(_mail2Id);
        mail2.IsRead.ShouldBeTrue();
        mail2.Folder.ShouldBe(EmailConstants.ClientAssignedFolder);
        mail2.ProcessedAt.ShouldNotBeNull();

        var mail1 = await ReadAsync(_mail1Id);
        mail1.ProcessedAt.ShouldBeNull();
        mail1.MessageId.ShouldBe(_mail1MessageId);
    }

    [Test]
    public async Task Control_StateSyncOnTheStaleFetchingScope_WouldOverwriteTheCommittedState()
    {
        var (fetchingScope, fetched) = await FetchTrackedAsync();
        using (fetchingScope)
        {
            await RunBatchAsync(fetched);

            await SyncReadStateAsync(fetchingScope, _mail2Id);
        }

        var mail2 = await ReadAsync(_mail2Id);
        mail2.IsRead.ShouldBeTrue();
        mail2.Folder.ShouldBe(InboxFolder);
        mail2.ProcessedAt.ShouldBeNull();
    }

    [Test]
    public async Task UniqueMessageIdIndex_HasNoSoftDeleteFilter_ASoftDeletedRowStillBlocksTheSameMessageId()
    {
        await using var context = NewContext();
        await context.ReceivedEmails.IgnoreQueryFilters()
            .Where(e => e.MessageId == _conflictMessageId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(e => e.IsDeleted, true));

        var repository = new ReceivedEmailRepository(context, Substitute.For<ILogger<ReceivedEmailRepository>>());
        (await repository.ExistsByMessageIdAsync(_conflictMessageId)).ShouldBeFalse();

        context.ReceivedEmails.Add(NewMail(Guid.NewGuid(), _conflictMessageId, imapUid: 4));
        var exception = await Should.ThrowAsync<DbUpdateException>(async () => await context.SaveChangesAsync());

        exception.InnerException.ShouldBeOfType<Npgsql.PostgresException>().SqlState.ShouldBe(UniqueViolationSqlState);
    }
}
