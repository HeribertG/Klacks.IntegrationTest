// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Security.Claims;

namespace Klacks.IntegrationTest.Mcp;

/// <summary>
/// Holds what the last MCP tool call saw: the principal the MCP SDK handed to the call handler and the
/// HttpContext.User that in-process mediator handlers (period closing, delete validators) read through
/// IHttpContextAccessor at the same moment.
/// </summary>
public sealed class McpCallRecorder
{
    public bool HttpContextAvailable { get; private set; }

    public ClaimsPrincipal? HttpContextUser { get; private set; }

    public ClaimsPrincipal? HandlerUser { get; private set; }

    public int CallCount { get; private set; }

    public void Record(bool httpContextAvailable, ClaimsPrincipal? httpContextUser, ClaimsPrincipal? handlerUser)
    {
        HttpContextAvailable = httpContextAvailable;
        HttpContextUser = httpContextUser;
        HandlerUser = handlerUser;
        CallCount++;
    }

    public void Reset()
    {
        HttpContextAvailable = false;
        HttpContextUser = null;
        HandlerUser = null;
        CallCount = 0;
    }
}
