using FluentAssertions;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Services.Settings;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories;
using Klacks.Api.Infrastructure.Services.Translation;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace IntegrationTest.Translation;

[TestFixture]
[Category("RealDatabase")]
[Category("ExternalApi")]
public class DeepLTranslationServiceTests
{
    private DataBaseContext _context = null!;
    private string _connectionString = null!;
    private ITranslationService _translationService = null!;
    private IMultiLanguageTranslationService _multiLanguageTranslationService = null!;
    private static string GetDataProtectionPath()
    {
        var klacksPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Klacks", "DataProtection-Keys");

        if (Directory.Exists(klacksPath))
        {
            return klacksPath;
        }

        var possibleWslPaths = new[]
        {
            "/mnt/c/Users/hgasp/AppData/Local/Klacks/DataProtection-Keys",
            "/mnt/c/Users/heribert/AppData/Local/Klacks/DataProtection-Keys"
        };

        foreach (var path in possibleWslPaths)
        {
            if (Directory.Exists(path))
            {
                return path;
            }
        }

        return klacksPath;
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
            ?? "Host=localhost;Port=5434;Database=klacks;Username=postgres;Password=admin";
    }

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(_connectionString)
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);

        var settingsRepository = CreateSettingsRepository();
        var encryptionService = CreateEncryptionServiceWithApiKeyOverride();
        var httpClient = new HttpClient();
        var logger = Substitute.For<ILogger<DeepLTranslationService>>();

        _translationService = new DeepLTranslationService(
            httpClient,
            settingsRepository,
            encryptionService,
            logger);

        _multiLanguageTranslationService = new MultiLanguageTranslationService(_translationService);
    }

    [TearDown]
    public void TearDown()
    {
        _context?.Dispose();
    }

    [Test]
    public void IsConfigured_WhenApiKeyExists_ShouldReturnTrue()
    {
        // Arrange
        // API Key should be configured in DB

        // Act
        var isConfigured = _translationService.IsConfigured;

        // Assert
        isConfigured.Should().BeTrue("DeepL API key should be configured in the database");
    }

    [Test]
    public async Task TranslateAsync_GermanToEnglish_ShouldReturnTranslation()
    {
        // Arrange
        var germanText = "Guten Morgen";
        var sourceLanguage = "de";
        var targetLanguage = "en";

        // Act
        var result = await _translationService.TranslateAsync(germanText, sourceLanguage, targetLanguage);

        // Assert
        result.Should().NotBeNull();
        result.TranslatedText.Should().NotBeNullOrEmpty();
        result.TranslatedText.ToLower().Should().Contain("morning", "Translation should contain 'morning'");
        result.SourceLanguage.Should().Be(sourceLanguage);
        result.TargetLanguage.Should().Be(targetLanguage);
    }

    [Test]
    public async Task TranslateAsync_EnglishToGerman_ShouldReturnTranslation()
    {
        // Arrange
        var englishText = "Good morning";
        var sourceLanguage = "en";
        var targetLanguage = "de";

        // Act
        var result = await _translationService.TranslateAsync(englishText, sourceLanguage, targetLanguage);

        // Assert
        result.Should().NotBeNull();
        result.TranslatedText.Should().NotBeNullOrEmpty();
        result.TranslatedText.ToLower().Should().ContainAny("guten", "morgen");
    }

    [Test]
    public async Task TranslateAsync_GermanToFrench_ShouldReturnTranslation()
    {
        // Arrange
        var germanText = "Ferien";
        var sourceLanguage = "de";
        var targetLanguage = "fr";

        // Act
        var result = await _translationService.TranslateAsync(germanText, sourceLanguage, targetLanguage);

        // Assert
        result.Should().NotBeNull();
        result.TranslatedText.Should().NotBeNullOrEmpty();
        result.TranslatedText.ToLower().Should().ContainAny("vacances", "congé");
    }

    [Test]
    public async Task TranslateAsync_GermanToItalian_ShouldReturnTranslation()
    {
        // Arrange
        var germanText = "Krankheit";
        var sourceLanguage = "de";
        var targetLanguage = "it";

        // Act
        var result = await _translationService.TranslateAsync(germanText, sourceLanguage, targetLanguage);

        // Assert
        result.Should().NotBeNull();
        result.TranslatedText.Should().NotBeNullOrEmpty();
        result.TranslatedText.ToLower().Should().ContainAny("malattia", "malato");
    }

    [Test]
    public async Task TranslateAsync_EmptyText_ShouldReturnEmptyResult()
    {
        // Arrange
        var emptyText = "";
        var sourceLanguage = "de";
        var targetLanguage = "en";

        // Act
        var result = await _translationService.TranslateAsync(emptyText, sourceLanguage, targetLanguage);

        // Assert
        result.Should().NotBeNull();
        result.TranslatedText.Should().BeEmpty();
    }

    [Test]
    public async Task TranslateAsync_WhitespaceText_ShouldReturnWhitespace()
    {
        // Arrange
        var whitespaceText = "   ";
        var sourceLanguage = "de";
        var targetLanguage = "en";

        // Act
        var result = await _translationService.TranslateAsync(whitespaceText, sourceLanguage, targetLanguage);

        // Assert
        result.Should().NotBeNull();
        result.TranslatedText.Should().Be(whitespaceText);
    }

    [Test]
    public async Task TranslateToAllLanguagesAsync_FromGerman_ShouldReturnAllLanguages()
    {
        // Arrange
        var germanText = "Schulung";
        var sourceLanguage = "de";

        // Act
        var results = await _translationService.TranslateToAllLanguagesAsync(germanText, sourceLanguage);

        // Assert
        results.Should().NotBeNull();
        results.Should().HaveCount(4);
        results.Should().ContainKey("de");
        results.Should().ContainKey("en");
        results.Should().ContainKey("fr");
        results.Should().ContainKey("it");

        results["de"].Should().Be(germanText, "Source language should keep original text");
        results["en"].Should().NotBeNullOrEmpty();
        results["fr"].Should().NotBeNullOrEmpty();
        results["it"].Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task TranslateToAllLanguagesAsync_FromEnglish_ShouldReturnAllLanguages()
    {
        // Arrange
        var englishText = "Training";
        var sourceLanguage = "en";

        // Act
        var results = await _translationService.TranslateToAllLanguagesAsync(englishText, sourceLanguage);

        // Assert
        results.Should().NotBeNull();
        results.Should().HaveCount(4);
        results["en"].Should().Be(englishText, "Source language should keep original text");
        results["de"].Should().NotBeNullOrEmpty();
        results["fr"].Should().NotBeNullOrEmpty();
        results["it"].Should().NotBeNullOrEmpty();
    }

    [Test]
    public void MultiLanguage_SupportedLanguages_ShouldContainAllFourLanguages()
    {
        // Arrange & Act
        var supportedLanguages = MultiLanguage.SupportedLanguages;

        // Assert
        supportedLanguages.Should().HaveCount(4);
        supportedLanguages.Should().Contain("de");
        supportedLanguages.Should().Contain("en");
        supportedLanguages.Should().Contain("fr");
        supportedLanguages.Should().Contain("it");
    }

    [Test]
    public async Task MultiLanguageTranslationService_TranslateEmptyFields_ShouldFillEmptyLanguages()
    {
        // Arrange
        var multiLanguage = new MultiLanguage
        {
            De = "Mitarbeiter",
            En = null,
            Fr = null,
            It = null
        };

        // Act
        var result = await _multiLanguageTranslationService.TranslateEmptyFieldsAsync(multiLanguage);

        // Assert
        result.Should().NotBeNull();
        result.De.Should().Be("Mitarbeiter", "Source language should keep original text");
        result.En.Should().NotBeNullOrEmpty("English should be translated");
        result.Fr.Should().NotBeNullOrEmpty("French should be translated");
        result.It.Should().NotBeNullOrEmpty("Italian should be translated");
    }

    [Test]
    public async Task MultiLanguageTranslationService_TranslateEmptyFields_FromEnglish_ShouldFillOtherLanguages()
    {
        // Arrange
        var multiLanguage = new MultiLanguage
        {
            De = null,
            En = "Employee",
            Fr = null,
            It = null
        };

        // Act
        var result = await _multiLanguageTranslationService.TranslateEmptyFieldsAsync(multiLanguage);

        // Assert
        result.Should().NotBeNull();
        result.En.Should().Be("Employee", "Source language should keep original text");
        result.De.Should().NotBeNullOrEmpty("German should be translated");
        result.Fr.Should().NotBeNullOrEmpty("French should be translated");
        result.It.Should().NotBeNullOrEmpty("Italian should be translated");
    }

    [Test]
    public async Task MultiLanguageTranslationService_WhenAllFieldsFilled_ShouldNotOverwrite()
    {
        // Arrange
        var multiLanguage = new MultiLanguage
        {
            De = "Deutsch",
            En = "English",
            Fr = "Francais",
            It = "Italiano"
        };

        // Act
        var result = await _multiLanguageTranslationService.TranslateEmptyFieldsAsync(multiLanguage);

        // Assert
        result.Should().NotBeNull();
        result.De.Should().Be("Deutsch");
        result.En.Should().Be("English");
        result.Fr.Should().Be("Francais");
        result.It.Should().Be("Italiano");
    }

    [Test]
    public async Task MultiLanguageTranslationService_WhenAllFieldsEmpty_ShouldReturnEmpty()
    {
        // Arrange
        var multiLanguage = new MultiLanguage
        {
            De = null,
            En = null,
            Fr = null,
            It = null
        };

        // Act
        var result = await _multiLanguageTranslationService.TranslateEmptyFieldsAsync(multiLanguage);

        // Assert
        result.Should().NotBeNull();
        result.De.Should().BeNull();
        result.En.Should().BeNull();
        result.Fr.Should().BeNull();
        result.It.Should().BeNull();
    }

    [Test]
    public async Task MultiLanguageTranslationService_WhenPartiallyFilled_ShouldOnlyFillEmpty()
    {
        // Arrange
        var multiLanguage = new MultiLanguage
        {
            De = "Test",
            En = "Existing",
            Fr = null,
            It = null
        };

        // Act
        var result = await _multiLanguageTranslationService.TranslateEmptyFieldsAsync(multiLanguage);

        // Assert
        result.Should().NotBeNull();
        result.De.Should().Be("Test", "Should keep original German");
        result.En.Should().Be("Existing", "Should keep existing English");
        result.Fr.Should().NotBeNullOrEmpty("French should be translated from German");
        result.It.Should().NotBeNullOrEmpty("Italian should be translated from German");
    }

    [Test]
    public void MultiLanguage_GetValue_ShouldReturnCorrectValue()
    {
        // Arrange
        var multiLanguage = new MultiLanguage
        {
            De = "Deutsch",
            En = "English",
            Fr = "Francais",
            It = "Italiano"
        };

        // Act & Assert
        multiLanguage.GetValue("de").Should().Be("Deutsch");
        multiLanguage.GetValue("en").Should().Be("English");
        multiLanguage.GetValue("fr").Should().Be("Francais");
        multiLanguage.GetValue("it").Should().Be("Italiano");
        multiLanguage.GetValue("DE").Should().Be("Deutsch", "Should be case-insensitive");
    }

    [Test]
    public void MultiLanguage_SetValue_ShouldSetCorrectValue()
    {
        // Arrange
        var multiLanguage = new MultiLanguage();

        // Act
        multiLanguage.SetValue("de", "Deutsch");
        multiLanguage.SetValue("en", "English");
        multiLanguage.SetValue("FR", "Francais");
        multiLanguage.SetValue("IT", "Italiano");

        // Assert
        multiLanguage.De.Should().Be("Deutsch");
        multiLanguage.En.Should().Be("English");
        multiLanguage.Fr.Should().Be("Francais");
        multiLanguage.It.Should().Be("Italiano");
    }

    private ISettingsRepository CreateSettingsRepository()
    {
        var filterService = Substitute.For<ICalendarRuleFilterService>();
        var sortingService = Substitute.For<ICalendarRuleSortingService>();
        var paginationService = Substitute.For<ICalendarRulePaginationService>();
        var macroManagementService = Substitute.For<IMacroManagementService>();
        var macroTypeManagementService = Substitute.For<IMacroTypeManagementService>();

        return new SettingsRepository(
            _context,
            filterService,
            sortingService,
            paginationService,
            macroManagementService,
            macroTypeManagementService
        );
    }

    private static ISettingsEncryptionService CreateEncryptionServiceWithApiKeyOverride()
    {
        var apiKeyFromEnv = Environment.GetEnvironmentVariable("DEEPL_API_KEY");

        if (!string.IsNullOrEmpty(apiKeyFromEnv))
        {
            var mockService = Substitute.For<ISettingsEncryptionService>();
            mockService.ProcessForReading("DEEPL_API_KEY", Arg.Any<string>())
                .Returns(apiKeyFromEnv);
            mockService.ProcessForReading(Arg.Is<string>(s => s != "DEEPL_API_KEY"), Arg.Any<string>())
                .Returns(x => x.ArgAt<string>(1));
            return mockService;
        }

        try
        {
            var dataProtectionPath = GetDataProtectionPath();
            var dataProtectionProvider = DataProtectionProvider.Create(
                new DirectoryInfo(dataProtectionPath),
                configuration => configuration.SetApplicationName("Klacks"));

            var logger = Substitute.For<ILogger<SettingsEncryptionService>>();

            return new SettingsEncryptionService(dataProtectionProvider, logger);
        }
        catch
        {
            var mockService = Substitute.For<ISettingsEncryptionService>();
            mockService.ProcessForReading(Arg.Any<string>(), Arg.Any<string>())
                .Returns(x => x.ArgAt<string>(1));
            return mockService;
        }
    }
}
