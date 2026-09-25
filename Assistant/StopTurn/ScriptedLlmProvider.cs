// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// A provider whose streamed answers come from a queue the test fills, in the order the provider is called (the
/// non-streaming calls of the post-turn helpers - memory extraction, grounding - get an empty answer and never
/// take a step, which is what keeps the queue deterministic): each step
/// is a piece of text, a set of tool calls, an endless stream that only ends when the turn is cancelled, or a
/// stream that breaks off. It behaves like the real streaming providers on a cancellation: the stream simply
/// ends and no exception is thrown. The factory hands this one provider out for every model.
/// </summary>

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;

namespace Klacks.IntegrationTest.Assistant.StopTurn;

public sealed record ScriptedCall(string Name, Dictionary<string, object>? Parameters = null);

public sealed record ScriptedStep(
    string Content,
    IReadOnlyList<ScriptedCall> Calls,
    bool StreamsUntilCancelled = false,
    bool BreaksOff = false)
{
    public static ScriptedStep Text(string content) => new(content, Array.Empty<ScriptedCall>());

    public static ScriptedStep Tools(params string[] skills) =>
        new(string.Empty, skills.Select(skill => new ScriptedCall(skill)).ToList());

    public static ScriptedStep EndlessText(string content) =>
        new(content, Array.Empty<ScriptedCall>(), StreamsUntilCancelled: true);

    public static ScriptedStep FailsAfter(string content) =>
        new(content, Array.Empty<ScriptedCall>(), BreaksOff: true);
}

public sealed class ScriptedLlmProvider : ILLMProvider
{
    public const string ProviderKey = "INTEGRATION_TEST_provider";
    private const int TokenLength = 4;
    private const int EndlessTokenDelayMs = 5;
    private const string BreakOffError = "connection reset by peer";

    private readonly ConcurrentQueue<ScriptedStep> _steps = new();
    private int _calls;
    private int _nonStreamingCalls;

    public string ProviderId => ProviderKey;

    public string ProviderName => ProviderKey;

    public bool IsEnabled => true;

    public bool SupportsStreaming => true;

    public int CallCount => Volatile.Read(ref _calls);

    public int NonStreamingCallCount => Volatile.Read(ref _nonStreamingCalls);

    public Action<CancellationToken>? OnStreamStarted { get; set; }

    public void Enqueue(params ScriptedStep[] steps)
    {
        foreach (var step in steps)
        {
            _steps.Enqueue(step);
        }
    }

    public void Reset()
    {
        _steps.Clear();
        Interlocked.Exchange(ref _calls, 0);
        Interlocked.Exchange(ref _nonStreamingCalls, 0);
        OnStreamStarted = null;
    }

    public void Configure(LLMProvider providerConfig)
    {
    }

    public Task<bool> ValidateApiKeyAsync(string apiKey) => Task.FromResult(true);

    public Task<LLMProviderResponse> ProcessAsync(LLMProviderRequest request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _nonStreamingCalls);
        return Task.FromResult(new LLMProviderResponse { Success = true, Content = string.Empty });
    }

    public async IAsyncEnumerable<string> ProcessStreamAsync(
        LLMProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var step = Next();
        OnStreamStarted?.Invoke(cancellationToken);
        await Task.Yield();

        foreach (var token in Split(step.Content))
        {
            yield return token;
        }

        if (step.BreaksOff)
        {
            throw new InvalidOperationException(BreakOffError);
        }

        if (step.StreamsUntilCancelled)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                yield return "…";
                await Task.Delay(EndlessTokenDelayMs, CancellationToken.None);
            }

            yield break;
        }

        for (var index = 0; index < step.Calls.Count; index++)
        {
            var call = step.Calls[index];
            yield return LLMStreamingTokens.ToolCallPrefix + JsonSerializer.Serialize(new
            {
                index,
                name = call.Name,
                arguments = JsonSerializer.Serialize(call.Parameters ?? new Dictionary<string, object>())
            });
        }

        if (step.Calls.Count > 0)
        {
            yield return LLMStreamingTokens.ToolCallEnd;
        }

        request.OnStreamUsage?.Invoke(new Klacks.Api.Domain.Services.Assistant.Providers.LLMUsage { InputTokens = 10, OutputTokens = 5 });
    }

    private ScriptedStep Next()
    {
        Interlocked.Increment(ref _calls);
        return _steps.TryDequeue(out var step) ? step : ScriptedStep.Text(string.Empty);
    }

    private static IEnumerable<string> Split(string content)
    {
        for (var start = 0; start < content.Length; start += TokenLength)
        {
            yield return content.Substring(start, Math.Min(TokenLength, content.Length - start));
        }
    }
}

public sealed class ScriptedLlmProviderFactory : ILLMProviderFactory
{
    private readonly ScriptedLlmProvider _provider;

    public ScriptedLlmProviderFactory(ScriptedLlmProvider provider) => _provider = provider;

    public Task<ILLMProvider?> GetProviderAsync(string providerId) => Task.FromResult<ILLMProvider?>(_provider);

    public Task<ILLMProvider?> GetProviderForModelAsync(string modelId) => Task.FromResult<ILLMProvider?>(_provider);

    public Task<List<ILLMProvider>> GetEnabledProvidersAsync() => Task.FromResult(new List<ILLMProvider> { _provider });
}
