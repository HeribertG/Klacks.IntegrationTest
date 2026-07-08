// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Net;
using System.Net.Http.Json;
using NUnit.Framework;

namespace Klacks.IntegrationTest.Messaging;

/// <summary>
/// Verifies the Telegram messaging webhook endpoint against a running Klacks.Api instance (localhost:5000).
/// Moved out of Klacks.E2ETest: these steps are pure backend HTTP calls with no Playwright/UI dependency.
/// </summary>
[TestFixture]
public class TelegramWebhookIntegrationTest
{
    private const string WebhookUrl = "http://localhost:5000/api/messaging/webhook/telegram";
    private const string WebhookUrlGet = "http://localhost:5000/api/messaging/webhook/telegram?hub.challenge=klacks";

    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    [Test]
    public async Task WebhookVerificationEndpoint_IsReachable()
    {
        TestContext.Out.WriteLine("=== Webhook verification endpoint reachable ===");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(WebhookUrlGet);
        }
        catch (HttpRequestException ex)
        {
            Assert.Inconclusive($"Backend API not reachable at {WebhookUrlGet}: {ex.Message}");
            return;
        }

        Assert.That(
            response.StatusCode,
            Is.EqualTo(HttpStatusCode.OK),
            "Webhook GET (verification) should return 200 OK");

        var body = await response.Content.ReadAsStringAsync();
        TestContext.Out.WriteLine($"Verification body: {body}");
        Assert.That(body, Does.Contain("klacks").Or.Contain("Webhook").IgnoreCase);
    }

    [Test]
    public async Task StartCommand_WithUnknownToken_ReturnsOk()
    {
        TestContext.Out.WriteLine("=== /start with unknown token is handled gracefully ===");

        var payload = new
        {
            message = new
            {
                message_id = 1,
                text = "/start not_a_real_token_xyz",
                chat = new { id = 999111L },
                from = new { id = 999111L, first_name = "E2E" },
            },
        };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsJsonAsync(WebhookUrl, payload);
        }
        catch (HttpRequestException ex)
        {
            Assert.Inconclusive($"Backend API not reachable at {WebhookUrl}: {ex.Message}");
            return;
        }

        Assert.That(
            response.StatusCode,
            Is.EqualTo(HttpStatusCode.OK),
            "Webhook should always return 200 for a structurally valid /start payload, even when the token is unknown");
    }

    [Test]
    public async Task StartCommand_WithoutToken_IsHandledGracefully()
    {
        TestContext.Out.WriteLine("=== /start without token falls through to default pipeline ===");

        var payload = new
        {
            message = new
            {
                message_id = 2,
                text = "/start",
                chat = new { id = 999112L },
                from = new { id = 999112L, first_name = "E2E" },
            },
        };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsJsonAsync(WebhookUrl, payload);
        }
        catch (HttpRequestException ex)
        {
            Assert.Inconclusive($"Backend API not reachable at {WebhookUrl}: {ex.Message}");
            return;
        }

        Assert.That(
            (int)response.StatusCode,
            Is.LessThan(500),
            "Webhook must not return a 5xx error for a token-less /start. Downstream 4xx (BadRequest/Unauthorized) is acceptable.");
    }

    [Test]
    public async Task StartCommand_WithMalformedPayload_DoesNotCrashServer()
    {
        TestContext.Out.WriteLine("=== Malformed /start payload is handled gracefully ===");

        var content = new StringContent("{ not valid json", System.Text.Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsync(WebhookUrl, content);
        }
        catch (HttpRequestException ex)
        {
            Assert.Inconclusive($"Backend API not reachable at {WebhookUrl}: {ex.Message}");
            return;
        }

        Assert.That(
            (int)response.StatusCode,
            Is.LessThan(500),
            "Webhook must not return a 5xx error for malformed JSON");
    }
}
