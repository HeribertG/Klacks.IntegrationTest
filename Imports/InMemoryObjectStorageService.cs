// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// In-memory replacement for the object storage so the ERP order import flow can be
/// integration-tested without external infrastructure. Object payloads are held in a
/// dictionary keyed by their full storage key. Size and SHA-256 go through ErpImportFileHasher, the same
/// hashing code path as the file-system implementation.
/// </summary>
using Klacks.Api.Domain.Interfaces.Imports;
using Klacks.Api.Domain.Models.Imports;
using Klacks.Api.Domain.Services.Imports;

namespace Klacks.IntegrationTest.Imports;

public class InMemoryObjectStorageService : IObjectStorageService
{
    private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _lastModifiedUtc = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> keys = _objects.Keys
            .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        return Task.FromResult(keys);
    }

    public Task<IReadOnlyList<StorageObjectMetadata>> ListWithMetadataAsync(string prefix, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<StorageObjectMetadata> objects = _objects
            .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new StorageObjectMetadata(pair.Key, pair.Value.LongLength, _lastModifiedUtc[pair.Key]))
            .ToList();

        return Task.FromResult(objects);
    }

    public Task<Stream> DownloadAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!_objects.TryGetValue(key, out var content))
        {
            throw new FileNotFoundException($"Object key not found: {key}");
        }

        return Task.FromResult<Stream>(new MemoryStream(content, writable: false));
    }

    public Task<bool> TryMoveAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken = default)
    {
        if (_objects.ContainsKey(destinationKey) || !_objects.Remove(sourceKey, out var content))
        {
            return Task.FromResult(false);
        }

        _objects[destinationKey] = content;
        if (_lastModifiedUtc.Remove(sourceKey, out var lastModified))
        {
            _lastModifiedUtc[destinationKey] = lastModified;
        }

        return Task.FromResult(true);
    }

    public async Task<StoredObjectInfo> UploadAsync(string key, Stream content, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        var info = await ErpImportFileHasher.CopyAndHashAsync(content, buffer, cancellationToken);
        _objects[key] = buffer.ToArray();
        _lastModifiedUtc[key] = DateTime.UtcNow;
        return info with { LastModifiedUtc = _lastModifiedUtc[key] };
    }

    public Task<StoredObjectInfo?> GetObjectInfoAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!_objects.TryGetValue(key, out var content))
        {
            return Task.FromResult<StoredObjectInfo?>(null);
        }

        using var stream = new MemoryStream(content, writable: false);
        return Task.FromResult<StoredObjectInfo?>(
            new StoredObjectInfo(content.LongLength, ErpImportFileHasher.ComputeSha256Hex(stream), _lastModifiedUtc[key]));
    }

    public void SetLastModified(string key, DateTime lastModifiedUtc)
    {
        _lastModifiedUtc[key] = DateTime.SpecifyKind(lastModifiedUtc, DateTimeKind.Utc);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        _objects.Remove(key);
        _lastModifiedUtc.Remove(key);
        return Task.CompletedTask;
    }

    public string ResolvePath(string key)
    {
        return $"in-memory://{key}";
    }

    public Task<ObjectStorageHealthResult> CheckHealthAsync(
        IReadOnlyList<string> requiredPrefixes,
        CancellationToken cancellationToken = default)
    {
        var prefixResults = requiredPrefixes
            .Select(prefix => new ObjectStoragePrefixHealth(prefix, true, null))
            .ToList();

        return Task.FromResult(new ObjectStorageHealthResult(
            "in-memory", true, true, true, null, prefixResults));
    }

    public bool Contains(string key)
    {
        return _objects.ContainsKey(key);
    }
}
