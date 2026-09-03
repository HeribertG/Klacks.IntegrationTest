// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// In-memory replacement for the object storage so the ERP order import flow can be
/// integration-tested without external infrastructure. Object payloads are held in a
/// dictionary keyed by their full storage key.
/// </summary>
using Klacks.Api.Domain.Interfaces.Imports;
using Klacks.Api.Domain.Models.Imports;

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

    public Task MoveAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken = default)
    {
        if (!_objects.Remove(sourceKey, out var content))
        {
            throw new FileNotFoundException($"Object key not found: {sourceKey}");
        }

        _objects[destinationKey] = content;
        if (_lastModifiedUtc.Remove(sourceKey, out var lastModified))
        {
            _lastModifiedUtc[destinationKey] = lastModified;
        }

        return Task.CompletedTask;
    }

    public Task<bool> TryClaimAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken = default)
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

    public async Task UploadAsync(string key, Stream content, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        _objects[key] = buffer.ToArray();
        _lastModifiedUtc[key] = DateTime.UtcNow;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!_objects.Remove(key))
        {
            throw new FileNotFoundException($"Object key not found: {key}");
        }

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
