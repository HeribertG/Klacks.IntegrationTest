// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The skill bridge of the stop-turn integration tests: it runs no real skill, so nothing but the test's own
/// rows is ever written. Each skill name can be given a behaviour (succeed at once, wait on a gate, fail) and
/// every call and the token it was handed are recorded, which is how a test sees that a write skill got no
/// stop token. A skill without a behaviour succeeds at once.
/// </summary>

using System.Collections.Concurrent;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Assistant.Skills;

namespace Klacks.IntegrationTest.Assistant.StopTurn;

public sealed class ScriptedSkillBridge : ILLMSkillBridge
{
    private readonly ConcurrentDictionary<string, Func<LLMFunctionCall, CancellationToken, Task<SkillBridgeResult>>> _behaviours =
        new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentQueue<(string Skill, CancellationToken Token)> Calls { get; } = new();

    public void On(string skill, Func<LLMFunctionCall, CancellationToken, Task<SkillBridgeResult>> behaviour) =>
        _behaviours[skill] = behaviour;

    public void Reset()
    {
        _behaviours.Clear();
        Calls.Clear();
    }

    public static SkillBridgeResult Succeeded(string message = "Done.") => new()
    {
        Success = true,
        ResultType = "Data",
        Message = message
    };

    public IReadOnlyList<LLMFunction> GetSkillsAsLLMFunctions(IReadOnlyList<string> userPermissions) =>
        Array.Empty<LLMFunction>();

    public IReadOnlyList<object> GetSkillsForProvider(LLMProviderType providerType, IReadOnlyList<string> userPermissions) =>
        Array.Empty<object>();

    public Task<SkillBridgeResult> ExecuteSkillFromLLMCallAsync(
        LLMFunctionCall functionCall, SkillExecutionContext context, CancellationToken cancellationToken = default)
    {
        Calls.Enqueue((functionCall.FunctionName, cancellationToken));
        return _behaviours.TryGetValue(functionCall.FunctionName, out var behaviour)
            ? behaviour(functionCall, cancellationToken)
            : Task.FromResult(Succeeded());
    }
}
