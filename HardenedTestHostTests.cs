// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the hardened test host: a booted HardenedTestWebApplicationFactory must not run a single
/// hosted service of Klacks' own beyond the four that have no switch and neither call out nor write
/// (ONNX warm-up, which reads its disable flag and returns, the ONNX idle unloader, the schedule time
/// zone check and the messaging field encryption bootstrap). A background service added later without a
/// BackgroundServices flag, or a flag the reflection in the factory does not reach, turns this red
/// instead of quietly talking to Slack, Telegram, IMAP or an LLM provider from the next test run.
/// Only Klacks' own types are checked: framework and library services (web host, data protection,
/// health-check publisher, the MCP SDK's session idle tracker) are not switchable from here.
/// It also pins that the switches beat appsettings.Development.json, which turns the Slack owner bridge
/// on, and that the knowledge index synchronizer is the no-op one.
/// </summary>

using Klacks.Api.Application.Configuration;
using Klacks.Api.Infrastructure.Services.Schedules;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Application.Services;
using Klacks.IntegrationTest.KnowledgeIndex;
using Klacks.IntegrationTest.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest;

[TestFixture]
[Category("TestHost")]
public class HardenedTestHostTests
{
    private const string KlacksNamespacePrefix = "Klacks.";
    private const string MessagingEncryptionInitializerName = "MessagingEncryptionInitializer";

    private static readonly HashSet<string> UnswitchedHostedServices = new(StringComparer.Ordinal)
    {
        nameof(OnnxWarmupService),
        nameof(OnnxSessionIdleUnloadService),
        nameof(ScheduleTimeZoneStartupCheckService),
        MessagingEncryptionInitializerName
    };

    [Test]
    public void HardenedHost_RunsNoSwitchableHostedService_AndNoKnowledgeIndexSync()
    {
        using var factory = new SignalRTestWebApplicationFactory();

        var hostedTypes = factory.Services.GetServices<IHostedService>()
            .Select(service => service.GetType())
            .ToList();
        TestContext.Out.WriteLine("Hosted services: " + string.Join(", ", hostedTypes.Select(type => type.FullName)));

        var unexpected = hostedTypes
            .Where(type => (type.Namespace ?? string.Empty).StartsWith(KlacksNamespacePrefix, StringComparison.Ordinal))
            .Where(type => !UnswitchedHostedServices.Contains(type.Name))
            .Select(type => type.FullName)
            .ToList();
        unexpected.ShouldBeEmpty("a test host must not start these hosted services");

        factory.Services.GetRequiredService<IOptions<BackgroundServiceOptions>>().Value.SlackOwnerBridge
            .ShouldBeFalse("appsettings.Development.json turns the Slack owner bridge on; the test host must win");

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IKnowledgeIndexSynchronizer>()
            .ShouldBeOfType<NoOpKnowledgeIndexSynchronizer>();
    }
}
