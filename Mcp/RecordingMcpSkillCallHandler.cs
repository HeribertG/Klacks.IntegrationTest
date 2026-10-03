// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Security.Claims;
using Klacks.Api.Presentation.Mcp;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;

namespace Klacks.IntegrationTest.Mcp;

/// <summary>
/// Stands in for the real MCP tool-call handler and only records which principal the call runs under,
/// so the pipeline test needs no skill, no LLM and no data beyond the caller.
/// </summary>
public sealed class RecordingMcpSkillCallHandler : IMcpSkillCallHandler
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly McpCallRecorder _recorder;

    public RecordingMcpSkillCallHandler(IHttpContextAccessor httpContextAccessor, McpCallRecorder recorder)
    {
        _httpContextAccessor = httpContextAccessor;
        _recorder = recorder;
    }

    public Task<CallToolResult> HandleAsync(
        CallToolRequestParams request,
        ClaimsPrincipal? user,
        CancellationToken cancellationToken)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        _recorder.Record(httpContext != null, httpContext?.User, user);

        return Task.FromResult(new CallToolResult
        {
            Content = [new TextContentBlock { Text = "recorded" }]
        });
    }
}
