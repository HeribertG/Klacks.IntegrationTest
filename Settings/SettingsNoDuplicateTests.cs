using FluentAssertions;
using Klacks.Api.Application.Commands.Settings.Settings;
using Klacks.Api.Application.Handlers.Settings.Setting;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories;
using Klacks.Api.Infrastructure.Repositories.Associations;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Repositories.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.Settings;

[TestFixture]
[Category("RealDatabase")]
public class SettingsNoDuplicateTests
{
    private DataBaseContext _context = null!;
    private string _connectionString = null!;
    private const string TestSettingTypePrefix = "INTEGRATION_TEST_SETTING_";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";

        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        using var context = new DataBaseContext(options, mockHttpContextAccessor);

        await CleanupTestDataWithContext(context);
    }

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);
    }

    [TearDown]
    public async Task TearDown()
    {
        await CleanupTestData();
        _context?.Dispose();
    }

    private async Task CleanupTestData()
    {
        await CleanupTestDataWithContext(_context);
    }

    private static async Task CleanupTestDataWithContext(DataBaseContext context)
    {
        var sql = $"DELETE FROM settings WHERE type LIKE '{TestSettingTypePrefix}%';";
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    [Test]
    public async Task PostCommandHandler_WhenSettingWithSameTypeExists_ShouldUpdateInsteadOfCreate()
    {
        // Arrange
        var testType = $"{TestSettingTypePrefix}DuplicateTest";
        var originalValue = "original_value";
        var updatedValue = "updated_value";

        var settingsRepository = CreateSettingsRepository();
        var unitOfWork = CreateUnitOfWork();
        var encryptionService = CreateEncryptionService();
        var logger = Substitute.For<ILogger<PostCommandHandler>>();

        var handler = new PostCommandHandler(settingsRepository, encryptionService, unitOfWork, logger);

        var firstSetting = new Klacks.Api.Domain.Models.Settings.Settings
        {
            Type = testType,
            Value = originalValue
        };
        var firstCommand = new PostCommand(firstSetting);

        // Act - First POST creates the setting
        var firstResult = await handler.Handle(firstCommand, CancellationToken.None);

        // Assert - Setting was created
        firstResult.Should().NotBeNull();
        firstResult!.Type.Should().Be(testType);
        firstResult.Value.Should().Be(originalValue);

        var countAfterFirstPost = await _context.Settings.CountAsync(s => s.Type == testType);
        countAfterFirstPost.Should().Be(1);

        // Arrange - Second POST with same type but different value
        var secondSetting = new Klacks.Api.Domain.Models.Settings.Settings
        {
            Type = testType,
            Value = updatedValue
        };
        var secondCommand = new PostCommand(secondSetting);

        // Act - Second POST should update, not create duplicate
        var secondResult = await handler.Handle(secondCommand, CancellationToken.None);

        // Assert - No duplicate created, value was updated
        secondResult.Should().NotBeNull();
        secondResult!.Type.Should().Be(testType);
        secondResult.Value.Should().Be(updatedValue);

        var countAfterSecondPost = await _context.Settings.CountAsync(s => s.Type == testType);
        countAfterSecondPost.Should().Be(1, "POST with existing type should update, not create duplicate");

        var finalSetting = await _context.Settings.FirstOrDefaultAsync(s => s.Type == testType);
        finalSetting.Should().NotBeNull();
        finalSetting!.Value.Should().Be(updatedValue);
    }

    [Test]
    public async Task PostCommandHandler_WhenSettingTypeDoesNotExist_ShouldCreateNewSetting()
    {
        // Arrange
        var testType = $"{TestSettingTypePrefix}NewSetting";
        var value = "test_value";

        var settingsRepository = CreateSettingsRepository();
        var unitOfWork = CreateUnitOfWork();
        var encryptionService = CreateEncryptionService();
        var logger = Substitute.For<ILogger<PostCommandHandler>>();

        var handler = new PostCommandHandler(settingsRepository, encryptionService, unitOfWork, logger);

        var setting = new Klacks.Api.Domain.Models.Settings.Settings
        {
            Type = testType,
            Value = value
        };
        var command = new PostCommand(setting);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.Type.Should().Be(testType);
        result.Value.Should().Be(value);
        result.Id.Should().NotBe(Guid.Empty);

        var dbSetting = await _context.Settings.FirstOrDefaultAsync(s => s.Type == testType);
        dbSetting.Should().NotBeNull();
    }

    [Test]
    public async Task PostCommandHandler_MultiplePostsWithSameType_ShouldNeverCreateDuplicates()
    {
        // Arrange
        var testType = $"{TestSettingTypePrefix}MultiplePosts";

        var settingsRepository = CreateSettingsRepository();
        var unitOfWork = CreateUnitOfWork();
        var encryptionService = CreateEncryptionService();
        var logger = Substitute.For<ILogger<PostCommandHandler>>();

        var handler = new PostCommandHandler(settingsRepository, encryptionService, unitOfWork, logger);

        // Act - Send 5 POSTs with the same type
        for (int i = 1; i <= 5; i++)
        {
            var setting = new Klacks.Api.Domain.Models.Settings.Settings
            {
                Type = testType,
                Value = $"value_{i}"
            };
            var command = new PostCommand(setting);
            await handler.Handle(command, CancellationToken.None);
        }

        // Assert - Only one entry should exist
        var count = await _context.Settings.CountAsync(s => s.Type == testType);
        count.Should().Be(1, "Multiple POSTs with same type should result in only one entry");

        var finalSetting = await _context.Settings.FirstOrDefaultAsync(s => s.Type == testType);
        finalSetting!.Value.Should().Be("value_5", "Last POST value should be persisted");
    }

    private ISettingsRepository CreateSettingsRepository()
    {
        var filterService = Substitute.For<ICalendarRuleFilterService>();
        var sortingService = Substitute.For<ICalendarRuleSortingService>();
        var paginationService = Substitute.For<ICalendarRulePaginationService>();
        var macroManagementService = Substitute.For<IMacroManagementService>();

        return new SettingsRepository(
            _context,
            filterService,
            sortingService,
            paginationService,
            macroManagementService
        );
    }

    private IUnitOfWork CreateUnitOfWork()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.CompleteAsync().Returns(_ =>
        {
            _context.SaveChanges();
            return Task.FromResult(1);
        });
        return unitOfWork;
    }

    private static ISettingsEncryptionService CreateEncryptionService()
    {
        var encryptionService = Substitute.For<ISettingsEncryptionService>();
        encryptionService.ProcessForStorage(Arg.Any<string>(), Arg.Any<string>())
            .Returns(x => x.ArgAt<string>(1));
        return encryptionService;
    }
}
