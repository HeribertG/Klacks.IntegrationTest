using FluentAssertions;
using Klacks.Api.Domain.Common;
using Klacks.Api.Presentation.Controllers.UserBackend;
using Klacks.Api.Presentation.DTOs.Config;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;

namespace IntegrationTest.Config;

[TestFixture]
[Category("Config")]
public class LanguageConfigIntegrationTests
{
    [Test]
    public void MultiLanguage_SupportedLanguages_ShouldContainAllExpectedLanguages()
    {
        // Act
        var supportedLanguages = MultiLanguage.SupportedLanguages;

        // Assert
        supportedLanguages.Should().NotBeNull();
        supportedLanguages.Should().HaveCount(4, "MultiLanguage should support exactly 4 languages");
        supportedLanguages.Should().Contain("de", "German should be supported");
        supportedLanguages.Should().Contain("en", "English should be supported");
        supportedLanguages.Should().Contain("fr", "French should be supported");
        supportedLanguages.Should().Contain("it", "Italian should be supported");

        Console.WriteLine("=== MultiLanguage.SupportedLanguages Test ===");
        Console.WriteLine($"Supported languages: [{string.Join(", ", supportedLanguages)}]");
        Console.WriteLine("=== TEST PASSED ===");
    }

    [Test]
    public void MultiLanguage_SupportedLanguages_ShouldBeDerivedFromProperties()
    {
        // Arrange
        var multiLanguage = new MultiLanguage();
        var propertyNames = typeof(MultiLanguage)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => p.Name.ToLowerInvariant())
            .ToArray();

        // Act
        var supportedLanguages = MultiLanguage.SupportedLanguages;

        // Assert
        supportedLanguages.Should().BeEquivalentTo(propertyNames,
            "SupportedLanguages should match all string properties of MultiLanguage class");

        Console.WriteLine("=== MultiLanguage Property Derivation Test ===");
        Console.WriteLine($"Properties found: [{string.Join(", ", propertyNames)}]");
        Console.WriteLine($"Supported languages: [{string.Join(", ", supportedLanguages)}]");
        Console.WriteLine("=== TEST PASSED ===");
    }

    [Test]
    public void LanguageConfig_FallbackOrder_ShouldHaveCorrectOrder()
    {
        // Act
        var fallbackOrder = LanguageConfig.FallbackOrder;

        // Assert
        fallbackOrder.Should().NotBeNull();
        fallbackOrder.Should().HaveCount(4, "FallbackOrder should contain exactly 4 languages");
        fallbackOrder[0].Should().Be("de", "German should be first fallback");
        fallbackOrder[1].Should().Be("fr", "French should be second fallback");
        fallbackOrder[2].Should().Be("it", "Italian should be third fallback");
        fallbackOrder[3].Should().Be("en", "English should be fourth fallback");

        Console.WriteLine("=== LanguageConfig.FallbackOrder Test ===");
        Console.WriteLine($"Fallback order: [{string.Join(" -> ", fallbackOrder)}]");
        Console.WriteLine("=== TEST PASSED ===");
    }

    [Test]
    public void LanguageConfig_FallbackOrder_ShouldContainOnlySupportedLanguages()
    {
        // Arrange
        var supportedLanguages = MultiLanguage.SupportedLanguages;

        // Act
        var fallbackOrder = LanguageConfig.FallbackOrder;

        // Assert
        foreach (var language in fallbackOrder)
        {
            supportedLanguages.Should().Contain(language,
                $"Fallback language '{language}' must be a supported language");
        }

        Console.WriteLine("=== FallbackOrder Validation Test ===");
        Console.WriteLine($"Supported: [{string.Join(", ", supportedLanguages)}]");
        Console.WriteLine($"Fallback:  [{string.Join(", ", fallbackOrder)}]");
        Console.WriteLine("All fallback languages are valid supported languages.");
        Console.WriteLine("=== TEST PASSED ===");
    }

    [Test]
    public void LanguageConfigController_GetLanguages_ShouldReturnCorrectResponse()
    {
        // Arrange
        var controller = new LanguageConfigController();

        // Act
        var result = controller.GetLanguages();

        // Assert
        result.Should().NotBeNull();
        result.Result.Should().BeOfType<OkObjectResult>();

        var okResult = result.Result as OkObjectResult;
        okResult.Should().NotBeNull();
        okResult!.Value.Should().BeOfType<LanguageConfigResponse>();

        var response = okResult.Value as LanguageConfigResponse;
        response.Should().NotBeNull();
        response!.SupportedLanguages.Should().BeEquivalentTo(MultiLanguage.SupportedLanguages);
        response.FallbackOrder.Should().BeEquivalentTo(LanguageConfig.FallbackOrder);

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
            FallbackOrder = ["de", "fr", "it", "en"]
        };

        // Assert
        response.SupportedLanguages.Should().HaveCount(4);
        response.FallbackOrder.Should().HaveCount(4);
        response.SupportedLanguages.Should().NotBeSameAs(response.FallbackOrder,
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
        multiLanguage.GetValue("de").Should().Be("Deutsch");
        multiLanguage.GetValue("en").Should().Be("English");
        multiLanguage.GetValue("fr").Should().Be("Français");
        multiLanguage.GetValue("it").Should().Be("Italiano");
        multiLanguage.GetValue("DE").Should().Be("Deutsch", "GetValue should be case-insensitive");
        multiLanguage.GetValue("unknown").Should().BeNull("Unknown language should return null");

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
        multiLanguage.De.Should().Be("Hallo");
        multiLanguage.En.Should().Be("Hello");
        multiLanguage.Fr.Should().Be("Bonjour");
        multiLanguage.It.Should().Be("Ciao");

        Console.WriteLine("=== MultiLanguage.SetValue Test ===");
        Console.WriteLine("All language values set correctly.");
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
        emptyMultiLanguage.IsEmpty.Should().BeTrue("MultiLanguage with no values should be empty");
        partialMultiLanguage.IsEmpty.Should().BeFalse("MultiLanguage with at least one value should not be empty");
        fullMultiLanguage.IsEmpty.Should().BeFalse("MultiLanguage with all values should not be empty");

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
        dictionary.Should().HaveCount(2, "Only non-empty values should be included");
        dictionary.Should().ContainKey("de");
        dictionary.Should().ContainKey("fr");
        dictionary.Should().NotContainKey("en", "Null values should not be included");
        dictionary.Should().NotContainKey("it", "Empty strings should not be included");

        Console.WriteLine("=== MultiLanguage.ToDictionary Test ===");
        Console.WriteLine($"Dictionary keys: [{string.Join(", ", dictionary.Keys)}]");
        Console.WriteLine("=== TEST PASSED ===");
    }
}
