// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Base of every in-process API test host that boots the real Program.cs. Beyond the test database
/// wiring it switches off everything the host would otherwise start on its own: every flag of
/// BackgroundServiceOptions plus the messaging plugin's inbound polling (Slack owner bridge, Telegram
/// polling, IMAP, LLM model sync, geocoding, agent triggers, embedding and learning workers all ran in
/// test hosts before, and the Slack bridge answered real Slack messages from a test run), the ONNX
/// warm-up (model download and ~2 GB per host) and the knowledge index sync, which re-embedded the
/// whole index on the next host start whenever a test had changed skill phrases.
/// The switches go through UseSetting because Program.cs reads BackgroundServiceOptions while it runs
/// its top-level code, before ConfigureAppConfiguration callbacks apply.
/// </summary>
/// <param name="RunsKnowledgeIndexSync">True only for a host whose job is to bring a fresh database up
/// to the production state, including the knowledge index the retrieval fixtures read</param>

using System.Reflection;
using Klacks.Api.Application.Configuration;
using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.IntegrationTest.KnowledgeIndex;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Klacks.IntegrationTest;

public abstract class HardenedTestWebApplicationFactory : WebApplicationFactory<Program>
{
    private const string TestEnvironment = "Development";

    /// <summary>
    /// Read by Klacks.Plugin.Messaging's registrar directly from configuration; it is not a property
    /// of BackgroundServiceOptions, so the reflection over that class does not reach it.
    /// </summary>
    private const string InboundMessagePollingSwitch = "InboundMessagePolling";

    protected virtual bool RunsKnowledgeIndexSync => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(TestEnvironment);
        TestHostDatabase.UseTestConnection(builder);
        DisableBackgroundServices(builder);
        builder.UseSetting(KnowledgeIndexConstants.WarmupEnabledConfigKey, bool.FalseString);

        if (!RunsKnowledgeIndexSync)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IKnowledgeIndexSynchronizer>();
                services.AddScoped<IKnowledgeIndexSynchronizer, NoOpKnowledgeIndexSynchronizer>();
            });
        }
    }

    private void DisableBackgroundServices(IWebHostBuilder builder)
    {
        var switches = typeof(BackgroundServiceOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(bool))
            .Select(property => property.Name)
            .Append(InboundMessagePollingSwitch);

        foreach (var name in switches)
        {
            var enabled = RunsKnowledgeIndexSync
                && name == nameof(BackgroundServiceOptions.KnowledgeIndexStartup);

            builder.UseSetting(
                ConfigurationPath.Combine(BackgroundServiceOptions.SectionName, name),
                enabled ? bool.TrueString : bool.FalseString);
        }
    }
}
