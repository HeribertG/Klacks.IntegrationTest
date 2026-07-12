using Shouldly;
using Klacks.Api.Domain.Common;
using Klacks.Api.Application.Interfaces.Settings;
using Klacks.Api.Presentation.Controllers.UserBackend;
using Klacks.Api.Application.DTOs.Config;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.Config;

[TestFixture]
[Category("Config")]
public class LanguageConfigIntegrationTests
{
    [Test]
    public void MultiLanguage_CoreLanguages_ShouldContainAllExpectedLanguages()
    {
        // Act
        var coreLanguages = MultiLanguage.CoreLanguages;

        // Assert
        coreLanguages.ShouldNotBeNull();
        coreLanguages.Count().ShouldBe(4, "MultiLanguage should support exactly 4 core languages");
        coreLanguages.ShouldContain("de", "German should be supported");
        coreLanguages.ShouldContain("en", "English should be supported");
        coreLanguages.ShouldContain("fr", "French should be supported");
        coreLanguages.ShouldContain("it", "Italian should be supported");

        Console.WriteLine("=== MultiLanguage.CoreLanguages Test ===");
        Console.WriteLine($"Core languages: [{string.Join(", ", coreLanguages)}]");
        Console.WriteLine("=== TEST PASSED ===");
    }

    [Test]
    public void LanguageConfig_FallbackOrder_ShouldHaveCorrectOrder()
    {
        // Act
        var fallbackOrder = LanguageConfig.FallbackOrder;

        // Assert
        fallbackOrder.ShouldNotBeNull();
        fallbackOrder.Count().ShouldBe(4, "FallbackOrder should contain exactly 4 languages");
        fallbackOrder[0].ShouldBe("de", "German should be first fallback");
        fallbackOrder[1].ShouldBe("fr", "French should be second fallback");
        fallbackOrder[2].ShouldBe("it", "Italian should be third fallback");
        fallbackOrder[3].ShouldBe("en", "English should be fourth fallback");

        Console.WriteLine("=== LanguageConfig.FallbackOrder Test ===");
        Console.WriteLine($"Fallback order: [{string.Join(" -> ", fallbackOrder)}]");
        Console.WriteLine("=== TEST PASSED ===");
    }

    [Test]
    public void LanguageConfig_FallbackOrder_ShouldContainOnlySupportedLanguages()
    {
        // Arrange
        var supportedLanguages = LanguageConfig.SupportedLanguages;

        // Act
        var fallbackOrder = LanguageConfig.FallbackOrder;

        // Assert
        foreach (var language in fallbackOrder)
        {
            supportedLanguages.ShouldContain(language,
                $"Fallback language '{language}' must be a supported language");
        }

        Console.WriteLine("=== FallbackOrder Validation Test ===");
        Console.WriteLine($"Supported: [{string.Join(", ", supportedLanguages)}]");
        Console.WriteLine($"Fallback:  [{string.Join(", ", fallbackOrder)}]");
        Console.WriteLine("All fallback languages are valid supported languages.");
        Console.WriteLine("=== TEST PASSED ===");
    }

    [Test]
    public async Task LanguageConfigController_GetLanguages_ShouldReturnCorrectResponse()
    {
        // Arrange
        var configuration = Substitute.For<IConfiguration>();
        var languagesSection = Substitute.For<IConfigurationSection>();
        var supportedSection = Substitute.For<IConfigurationSection>();
        var fallbackSection = Substitute.For<IConfigurationSection>();
        var metadataSection = Substitute.For<IConfigurationSection>();

        configuration.GetSection("Languages").Returns(languagesSection);
        languagesSection.GetSection("Supported").Returns(supportedSection);
        languagesSection.GetSection("FallbackOrder").Returns(fallbackSection);
        languagesSection.GetSection("Metadata").Returns(metadataSection);

        var languagePluginService = Substitute.For<ILanguagePluginService>();
        languagePluginService.GetInstalledPluginCodes().Returns(new List<string>());
        var featurePluginService = Substitute.For<Klacks.Api.Application.Interfaces.Plugins.IFeaturePluginService>();
        var marketplaceClient = Substitute.For<IMarketplaceClientService>();
        var settingsReader = Substitute.For<Klacks.Api.Domain.Interfaces.Settings.ISettingsReader>();
        settingsReader.GetSetting(Arg.Any<string>()).Returns((Klacks.Api.Domain.Models.Settings.Settings?)null);
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<LanguageConfigController>>();
        var controller = new LanguageConfigController(configuration, languagePluginService, featurePluginService, marketplaceClient, settingsReader, logger);

        // Act
        var result = await controller.GetLanguages();

        // Assert
        result.ShouldNotBeNull();
        result.Result.ShouldBeOfType<OkObjectResult>();

        var okResult = result.Result as OkObjectResult;
        okResult.ShouldNotBeNull();
        okResult!.Value.ShouldBeOfType<LanguageConfigResponse>();

        var response = okResult.Value as LanguageConfigResponse;
        response.ShouldNotBeNull();
        response!.SupportedLanguages.ShouldBeEquivalentTo(LanguageConfig.SupportedLanguages);
        response.FallbackOrder.ShouldBeEquivalentTo(LanguageConfig.FallbackOrder);

        Console.WriteLine("=== LanguageConfigController.GetLanguages Test ===");
        Console.WriteLine($"SupportedLanguages: [{string.Join(", ", response.SupportedLanguages)}]");
        Console.WriteLine($"FallbackOrder: [{string.Join(", ", response.FallbackOrder)}]");
        Console.WriteLine("=== TEST PASSED ===");
    }

    [Test]
    public void LanguageConfigResponse_ShouldHaveCorrectStructure()
    {
        // Arrange & Act
        var response = new LanguageConfigResponse
        {
            SupportedLanguages = ["de", "en", "fr", "it"],
            FallbackOrder = ["de", "fr", "it", "en"],
            Metadata = new Dictionary<string, LanguageMetadata>
            {
                ["de"] = new LanguageMetadata { Name = "German", DisplayName = "Deutsch", SpeechLocale = "de-CH" }
            }
        };

        // Assert
        response.SupportedLanguages.Length.ShouldBe(4);
        response.FallbackOrder.Length.ShouldBe(4);
        response.Metadata.Count.ShouldBe(1);
        response.Metadata["de"].DisplayName.ShouldBe("Deutsch");
        response.SupportedLanguages.ShouldNotBeSameAs(response.FallbackOrder,
            "SupportedLanguages and FallbackOrder should be independent arrays");

        Console.WriteLine("=== LanguageConfigResponse Structure Test ===");
        Console.WriteLine("Response structure is correct.");
        Console.WriteLine("=== TEST PASSED ===");
    }

    [Test]
    public void MultiLanguage_GetValue_ShouldReturnCorrectValueForLanguage()
    {
        // Arrange
        var multiLanguage = new MultiLanguage
        {
            De = "Deutsch",
            En = "English",
            Fr = "Français",
            It = "Italiano"
        };

        // Act & Assert
        multiLanguage.GetValue("de").ShouldBe("Deutsch");
        multiLanguage.GetValue("en").ShouldBe("English");
        multiLanguage.GetValue("fr").ShouldBe("Français");
        multiLanguage.GetValue("it").ShouldBe("Italiano");
        multiLanguage.GetValue("DE").ShouldBe("Deutsch", "GetValue should be case-insensitive");
        multiLanguage.GetValue("unknown").ShouldBeNull("Unknown language should return null");

        Console.WriteLine("=== MultiLanguage.GetValue Test ===");
        Console.WriteLine("All language values retrieved correctly.");
        Console.WriteLine("=== TEST PASSED ===");
    }

    [Test]
    public void MultiLanguage_SetValue_ShouldSetCorrectValueForLanguage()
    {
        // Arrange
        var multiLanguage = new MultiLanguage();

        // Act
        multiLanguage.SetValue("de", "Hallo");
        multiLanguage.SetValue("EN", "Hello");
        multiLanguage.SetValue("fr", "Bonjour");
        multiLanguage.SetValue("it", "Ciao");

        // Assert
        multiLanguage.De.ShouldBe("Hallo");
        multiLanguage.En.ShouldBe("Hello");
        multiLanguage.Fr.ShouldBe("Bonjour");
        multiLanguage.It.ShouldBe("Ciao");

        Console.WriteLine("=== MultiLanguage.SetValue Test ===");
        Console.WriteLine("All language values set correctly.");
        Console.WriteLine("=== TEST PASSED ===");
    }

    [Test]
    public void MultiLanguage_SetValue_ShouldSupportDynamicLanguages()
    {
        // Arrange
        var multiLanguage = new MultiLanguage();

        // Act
        multiLanguage.SetValue("es", "Hola");
        multiLanguage.SetValue("pt", "Olá");

        // Assert
        multiLanguage.GetValue("es").ShouldBe("Hola");
        multiLanguage.GetValue("pt").ShouldBe("Olá");

        Console.WriteLine("=== MultiLanguage Dynamic Languages Test ===");
        Console.WriteLine("Dynamic language values set and retrieved correctly.");
        Console.WriteLine("=== TEST PASSED ===");
    }

    [Test]
    public void MultiLanguage_IsEmpty_ShouldReturnTrueWhenAllValuesAreNull()
    {
        // Arrange
        var emptyMultiLanguage = new MultiLanguage();
        var partialMultiLanguage = new MultiLanguage { De = "Test" };
        var fullMultiLanguage = new MultiLanguage
        {
            De = "De",
            En = "En",
            Fr = "Fr",
            It = "It"
        };

        // Assert
        emptyMultiLanguage.IsEmpty.ShouldBeTrue("MultiLanguage with no values should be empty");
        partialMultiLanguage.IsEmpty.ShouldBeFalse("MultiLanguage with at least one value should not be empty");
        fullMultiLanguage.IsEmpty.ShouldBeFalse("MultiLanguage with all values should not be empty");

        Console.WriteLine("=== MultiLanguage.IsEmpty Test ===");
        Console.WriteLine("IsEmpty property works correctly.");
        Console.WriteLine("=== TEST PASSED ===");
    }

    [Test]
    public void MultiLanguage_ToDictionary_ShouldOnlyIncludeNonEmptyValues()
    {
        // Arrange
        var multiLanguage = new MultiLanguage
        {
            De = "Deutsch",
            En = null,
            Fr = "Français",
            It = ""
        };

        // Act
        var dictionary = multiLanguage.ToDictionary();

        // Assert
        dictionary.Count.ShouldBe(2, "Only non-empty values should be included");
        dictionary.ShouldContainKey("de");
        dictionary.ShouldContainKey("fr");
        dictionary.ShouldNotContainKey("en", "Null values should not be included");
        dictionary.ShouldNotContainKey("it", "Empty strings should not be included");

        Console.WriteLine("=== MultiLanguage.ToDictionary Test ===");
        Console.WriteLine($"Dictionary keys: [{string.Join(", ", dictionary.Keys)}]");
        Console.WriteLine("=== TEST PASSED ===");
    }

    [Test]
    public void MultiLanguage_GetPopulatedLanguages_ShouldReturnOnlyPopulatedKeys()
    {
        // Arrange
        var multiLanguage = new MultiLanguage
        {
            De = "Deutsch",
            Fr = "Français"
        };
        multiLanguage.SetValue("es", "Español");

        // Act
        var populatedLanguages = multiLanguage.GetPopulatedLanguages().ToList();

        // Assert
        populatedLanguages.Count.ShouldBe(3);
        populatedLanguages.ShouldContain("de");
        populatedLanguages.ShouldContain("fr");
        populatedLanguages.ShouldContain("es");

        Console.WriteLine("=== MultiLanguage.GetPopulatedLanguages Test ===");
        Console.WriteLine($"Populated languages: [{string.Join(", ", populatedLanguages)}]");
        Console.WriteLine("=== TEST PASSED ===");
    }
}
