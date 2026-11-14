// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Setting values the OAuth flow tests set per scenario without writing rows into the shared settings table. Keys that
/// are not overridden fall through to the real settings repository, so the production settings query still runs
/// against Npgsql for everything else.
/// </summary>

using System.Collections.Concurrent;

namespace Klacks.IntegrationTest.OAuth;

public sealed class OAuthSettingOverrides
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    public void Set(string key, string value) => _values[key] = value;

    public void Clear() => _values.Clear();

    public bool TryGet(string key, out string value)
    {
        return _values.TryGetValue(key, out value!);
    }
}
