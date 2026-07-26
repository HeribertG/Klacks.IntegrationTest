// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Email;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Email;
using Klacks.IntegrationTest;
using Klacks.IntegrationTest.SignalR;
using Klacks.IntegrationTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Assistant;

/// <summary>
/// Runs the real email-intent JSON-extraction prompt (EmailIntentAnalysisService.BuildPrompt) against
/// every currently-enabled model in the Dev DB, one NUnit test case per model, so a specific provider
/// can be re-run on demand ("on the fly") whenever the prompt or the non-conversational system-prompt
/// handling changes. Only checks structural JSON validity (the object parses and carries a non-empty
/// intent) — model choice of intent/content is out of scope, this guards the parsing contract that
/// broke in production when the full Klacksy agent persona leaked into a one-shot extraction call.
///
/// [Explicit] + [Category("Llm")] + [Category("RealDatabase")]: makes real LLM API calls against
/// whatever providers are configured in the Dev DB (5434), costs money, needs network. A model with no
/// working provider/key Assert.Ignores instead of failing obscurely; a model that answers but returns
/// unparsable JSON fails the test for that model specifically.
/// </summary>
[TestFixture]
[Explicit("Email-analysis LLM JSON robustness — makes real LLM calls per enabled provider in the Dev DB (5434); costs money, local-only")]
[Category("Llm")]
[Category("RealDatabase")]
public class EmailAnalysisLlmJsonRobustnessTests
{
    private const string TestFromAddress = "llm-robustness-test@example.com";
    private const string TestSubject = "Verfügbarkeit";
    private const string TestBody =
        "Hallo, ich kann im August jeden Dienstag nur zwischen 06:00 und 17:00 Uhr arbeiten. Danke!";

    private static readonly ScheduleCommandKeywordSet DefaultKeywords = ScheduleCommandKeywordTestFactory.Default;

    private static readonly string[] ProviderUnavailableMarkers =
    [
        "not available", "api key", "provider", "unhealthy", "not configured", "no usable"
    ];

    private SignalRTestWebApplicationFactory _factory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _factory = new SignalRTestWebApplicationFactory();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _factory?.Dispose();
    }

    private static IEnumerable<string> EnabledModelIds()
    {
        using var connection = new NpgsqlConnection(TestHostDatabase.ConnectionString);
        connection.Open();

        using var command = new NpgsqlCommand(
            "SELECT model_id FROM llm_models WHERE is_enabled = true AND is_deleted = false ORDER BY provider_id, model_id",
            connection);
        using var reader = command.ExecuteReader();

        var modelIds = new List<string>();
        while (reader.Read())
        {
            modelIds.Add(reader.GetString(0));
        }

        return modelIds;
    }

    [TestCaseSource(nameof(EnabledModelIds))]
    public async Task EmailIntentPrompt_OnEnabledModel_ReturnsParsableJson(string modelId)
    {
        using var scope = _factory.Services.CreateScope();
        var llmService = scope.ServiceProvider.GetRequiredService<ILLMService>();
        var audienceResolver = scope.ServiceProvider.GetRequiredService<IPlanningAudienceResolver>();

        var userId = await audienceResolver.GetFirstAdminUserIdAsync();
        if (string.IsNullOrEmpty(userId))
        {
            Assert.Ignore("No admin user configured in the Dev DB — cannot open an LLM conversation.");
        }

        var email = new ReceivedEmail
        {
            FromAddress = TestFromAddress,
            Subject = TestSubject,
            BodyText = TestBody,
            ReceivedDate = DateTime.UtcNow,
        };

        var context = new LLMContext
        {
            Message = EmailIntentAnalysisService.BuildPrompt(email, EntityTypeEnum.Employee, TestBody, DefaultKeywords),
            ModelId = modelId,
            UserId = userId,
            IsNonConversational = true,
        };

        LLMResponse response;
        try
        {
            response = await llmService.ProcessAsync(context);
        }
        catch (Exception ex) when (LooksLikeProviderUnavailable(ex.Message))
        {
            Assert.Ignore($"Model '{modelId}' has no usable provider: {ex.Message}");
            return;
        }

        if (LooksLikeProviderUnavailable(response.Message))
        {
            Assert.Ignore($"Model '{modelId}' reported unavailable: {response.Message}");
            return;
        }

        var parsed = EmailIntentAnalysisService.ParseReply(response.Message);

        parsed.ShouldNotBeNull(
            $"Model '{modelId}' did not return parsable JSON. Raw reply: {Truncate(response.Message)}");
        parsed!.Intent.ShouldNotBeNullOrWhiteSpace(
            $"Model '{modelId}' returned JSON without an 'intent' field. Raw reply: {Truncate(response.Message)}");

        TestContext.Out.WriteLine($"Model '{modelId}': intent={parsed.Intent}, summary={parsed.Summary}");
    }

    private static bool LooksLikeProviderUnavailable(string text)
    {
        var lower = text.ToLowerInvariant();
        return ProviderUnavailableMarkers.Any(lower.Contains);
    }

    private static string Truncate(string value) => value.Length <= 300 ? value : value[..300] + "...";
}
