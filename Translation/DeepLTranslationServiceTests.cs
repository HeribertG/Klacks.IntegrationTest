using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Translation;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Services.Settings;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Settings;
using Klacks.Api.Infrastructure.Services.Translation;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Translation;

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
        var backendKeysPath = FindBackendDataProtectionPath();
        if (backendKeysPath != null)
        {
            return backendKeysPath;
        }

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

    private static string? FindBackendDataProtectionPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, "Klacks.Api", "DataProtection-Keys");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
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
            .UseSnakeCaseNamingConvention()
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

    private async Task EnsureDeepLConfiguredOrIgnoreAsync()
    {
        if (!await _translationService.IsConfiguredAsync())
        {
            Assert.Ignore(
                "DeepL API key is not configured or not decryptable in this environment. " +
                "Set the DEEPL_API_KEY environment variable to run the real translation tests.");
        }
    }

    [Test]
    public async Task IsConfigured_WhenApiKeyExists_ShouldReturnTrue()
    {
        await EnsureDeepLConfiguredOrIgnoreAsync();

        var isConfigured = await _translationService.IsConfiguredAsync();

        isConfigured.ShouldBeTrue("DeepL API key should be configured in the database");
    }

    [Test]
    public async Task TranslateAsync_GermanToEnglish_ShouldReturnTranslation()
    {
        await EnsureDeepLConfiguredOrIgnoreAsync();

        // Arrange
        var germanText = "Guten Morgen";
        var sourceLanguage = "de";
        var targetLanguage = "en";

        // Act
        var result = await _translationService.TranslateAsync(germanText, sourceLanguage, targetLanguage);

        // Assert
        result.ShouldNotBeNull();
        result.TranslatedText.ShouldNotBeNullOrEmpty();
        result.TranslatedText.ToLower().ShouldContain("morning");
        result.SourceLanguage.ShouldBe(sourceLanguage);
        result.TargetLanguage.ShouldBe(targetLanguage);
    }

    [Test]
    public async Task TranslateAsync_EnglishToGerman_ShouldReturnTranslation()
    {
        await EnsureDeepLConfiguredOrIgnoreAsync();

        // Arrange
        var englishText = "Good morning";
        var sourceLanguage = "en";
        var targetLanguage = "de";

        // Act
        var result = await _translationService.TranslateAsync(englishText, sourceLanguage, targetLanguage);

        // Assert
        result.ShouldNotBeNull();
        result.TranslatedText.ShouldNotBeNullOrEmpty();
        (result.TranslatedText.ToLower().Contains("guten") || result.TranslatedText.ToLower().Contains("morgen")).ShouldBeTrue();
    }

    [Test]
    public async Task TranslateAsync_GermanToFrench_ShouldReturnTranslation()
    {
        await EnsureDeepLConfiguredOrIgnoreAsync();

        // Arrange
        var germanText = "Ferien";
        var sourceLanguage = "de";
        var targetLanguage = "fr";

        // Act
        var result = await _translationService.TranslateAsync(germanText, sourceLanguage, targetLanguage);

        // Assert
        result.ShouldNotBeNull();
        result.TranslatedText.ShouldNotBeNullOrEmpty();
        (result.TranslatedText.ToLower().Contains("vacances") || result.TranslatedText.ToLower().Contains("congé")).ShouldBeTrue();
    }

    [Test]
    public async Task TranslateAsync_GermanToItalian_ShouldReturnTranslation()
    {
        await EnsureDeepLConfiguredOrIgnoreAsync();

        // Arrange
        var germanText = "Krankheit";
        var sourceLanguage = "de";
        var targetLanguage = "it";

        // Act
        var result = await _translationService.TranslateAsync(germanText, sourceLanguage, targetLanguage);

        // Assert
        result.ShouldNotBeNull();
        result.TranslatedText.ShouldNotBeNullOrEmpty();
        (result.TranslatedText.ToLower().Contains("malattia") || result.TranslatedText.ToLower().Contains("malato")).ShouldBeTrue();
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
        result.ShouldNotBeNull();
        result.TranslatedText.ShouldBeEmpty();
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
        result.ShouldNotBeNull();
        result.TranslatedText.ShouldBe(whitespaceText);
    }

    [Test]
    public async Task TranslateToAllLanguagesAsync_FromGerman_ShouldReturnAllLanguages()
    {
        await EnsureDeepLConfiguredOrIgnoreAsync();

        // Arrange
        var germanText = "Schulung";
        var sourceLanguage = "de";

        // Act
        var results = await _translationService.TranslateToAllLanguagesAsync(germanText, sourceLanguage);

        // Assert
        results.ShouldNotBeNull();
        results.Count.ShouldBe(4);
        results.ShouldContainKey("de");
        results.ShouldContainKey("en");
        results.ShouldContainKey("fr");
        results.ShouldContainKey("it");

        results["de"].ShouldBe(germanText, "Source language should keep original text");
        results["en"].ShouldNotBeNullOrEmpty();
        results["fr"].ShouldNotBeNullOrEmpty();
        results["it"].ShouldNotBeNullOrEmpty();
    }

    [Test]
    public async Task TranslateToAllLanguagesAsync_FromEnglish_ShouldReturnAllLanguages()
    {
        await EnsureDeepLConfiguredOrIgnoreAsync();

        // Arrange
        var englishText = "Training";
        var sourceLanguage = "en";

        // Act
        var results = await _translationService.TranslateToAllLanguagesAsync(englishText, sourceLanguage);

        // Assert
        results.ShouldNotBeNull();
        results.Count.ShouldBe(4);
        results["en"].ShouldBe(englishText, "Source language should keep original text");
        results["de"].ShouldNotBeNullOrEmpty();
        results["fr"].ShouldNotBeNullOrEmpty();
        results["it"].ShouldNotBeNullOrEmpty();
    }

    [Test]
    public async Task TranslateAsync_HtmlWithStyleBlockAndNestedLink_ShouldPreserveMarkupAndStyle()
    {
        await EnsureDeepLConfiguredOrIgnoreAsync();

        // Arrange - representative real-world newsletter-style HTML email
        var html = """
            <html>
            <head>
            <style>
              body { font-family: Arial, sans-serif; color: #333333; }
              .header { background-color: #ff0000; padding: 10px; }
              a { color: blue; text-decoration: none; }
            </style>
            </head>
            <body>
              <div class="header"><h1>Welcome to our newsletter</h1></div>
              <p>Dear customer, thank you for <a href="https://example.com">visiting our store</a> last week. We hope you enjoyed your experience.</p>
              <p>Best regards,<br>The Team</p>
            </body>
            </html>
            """;

        // Act
        var result = await _translationService.TranslateAsync(html, "en", "de", isHtml: true);

        // Assert / empirical inspection - dump raw output so a human can judge markup fidelity
        TestContext.Out.WriteLine("=== TRANSLATED HTML OUTPUT ===");
        TestContext.Out.WriteLine(result.TranslatedText);
        TestContext.Out.WriteLine("=== END ===");

        result.TranslatedText.ShouldContain("<style>", Case.Insensitive, "style block must survive as a tag");
        result.TranslatedText.ShouldContain("background-color: #ff0000", Case.Insensitive, "CSS property/value must not be mangled by translation");
        result.TranslatedText.ShouldContain("font-family: Arial, sans-serif", Case.Insensitive, "CSS font-family value must not be translated");
        result.TranslatedText.ShouldContain("href=\"https://example.com\"", Case.Insensitive, "link target must survive unchanged");
        result.TranslatedText.ShouldContain("<a ", Case.Insensitive, "anchor tag must remain a tag, not be flattened into text");
    }

    [Test]
    public void MultiLanguage_SupportedLanguages_ShouldContainAllFourLanguages()
    {
        // Arrange & Act
        var supportedLanguages = MultiLanguage.SupportedLanguages;

        // Assert
        supportedLanguages.Count().ShouldBe(4);
        supportedLanguages.ShouldContain("de");
        supportedLanguages.ShouldContain("en");
        supportedLanguages.ShouldContain("fr");
        supportedLanguages.ShouldContain("it");
    }

    [Test]
    public async Task MultiLanguageTranslationService_TranslateEmptyFields_ShouldFillEmptyLanguages()
    {
        await EnsureDeepLConfiguredOrIgnoreAsync();

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
        result.ShouldNotBeNull();
        result.De.ShouldBe("Mitarbeiter", "Source language should keep original text");
        result.En.ShouldNotBeNullOrEmpty("English should be translated");
        result.Fr.ShouldNotBeNullOrEmpty("French should be translated");
        result.It.ShouldNotBeNullOrEmpty("Italian should be translated");
    }

    [Test]
    public async Task MultiLanguageTranslationService_TranslateEmptyFields_FromEnglish_ShouldFillOtherLanguages()
    {
        await EnsureDeepLConfiguredOrIgnoreAsync();

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
        result.ShouldNotBeNull();
        result.En.ShouldBe("Employee", "Source language should keep original text");
        result.De.ShouldNotBeNullOrEmpty("German should be translated");
        result.Fr.ShouldNotBeNullOrEmpty("French should be translated");
        result.It.ShouldNotBeNullOrEmpty("Italian should be translated");
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
        result.ShouldNotBeNull();
        result.De.ShouldBe("Deutsch");
        result.En.ShouldBe("English");
        result.Fr.ShouldBe("Francais");
        result.It.ShouldBe("Italiano");
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
        result.ShouldNotBeNull();
        result.De.ShouldBeNull();
        result.En.ShouldBeNull();
        result.Fr.ShouldBeNull();
        result.It.ShouldBeNull();
    }

    [Test]
    public async Task MultiLanguageTranslationService_WhenPartiallyFilled_ShouldOnlyFillEmpty()
    {
        await EnsureDeepLConfiguredOrIgnoreAsync();

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
        result.ShouldNotBeNull();
        result.De.ShouldBe("Test", "Should keep original German");
        result.En.ShouldBe("Existing", "Should keep existing English");
        result.Fr.ShouldNotBeNullOrEmpty("French should be translated from German");
        result.It.ShouldNotBeNullOrEmpty("Italian should be translated from German");
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
        multiLanguage.GetValue("de").ShouldBe("Deutsch");
        multiLanguage.GetValue("en").ShouldBe("English");
        multiLanguage.GetValue("fr").ShouldBe("Francais");
        multiLanguage.GetValue("it").ShouldBe("Italiano");
        multiLanguage.GetValue("DE").ShouldBe("Deutsch", "Should be case-insensitive");
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
        multiLanguage.De.ShouldBe("Deutsch");
        multiLanguage.En.ShouldBe("English");
        multiLanguage.Fr.ShouldBe("Francais");
        multiLanguage.It.ShouldBe("Italiano");
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
