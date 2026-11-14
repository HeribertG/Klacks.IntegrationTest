// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Settings reader for the OAuth flow tests: returns an override when one is set for the key and otherwise asks the
/// real settings repository.
/// </summary>
/// <param name="inner">The real settings repository</param>
/// <param name="overrides">Values set by the running test</param>

using SettingEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.IntegrationTest.OAuth;

public sealed class OverridableSettingsReader : ISettingsReader
{
    private readonly ISettingsReader _inner;
    private readonly OAuthSettingOverrides _overrides;

    public OverridableSettingsReader(ISettingsReader inner, OAuthSettingOverrides overrides)
    {
        _inner = inner;
        _overrides = overrides;
    }

    public async Task<SettingEntity?> GetSetting(string type)
    {
        if (_overrides.TryGet(type, out var value))
        {
            return new SettingEntity { Type = type, Value = value };
        }

        return await _inner.GetSetting(type);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetSettingsByTypesAsync(
        IEnumerable<string> types,
        CancellationToken cancellationToken = default)
    {
        var keys = types.ToList();
        var fromDatabase = await _inner.GetSettingsByTypesAsync(keys, cancellationToken);
        var merged = new Dictionary<string, string>(fromDatabase, StringComparer.Ordinal);

        foreach (var key in keys)
        {
            if (_overrides.TryGet(key, out var value))
            {
                merged[key] = value;
            }
        }

        return merged;
    }
}
