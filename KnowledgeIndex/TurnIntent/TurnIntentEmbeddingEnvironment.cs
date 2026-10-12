// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Shared set-up of the turn-intent probe and trainer: loads the language-plugin keywords into
/// MutationIntentDetector exactly as Program.cs does, locates the Klacks.Api source directory and embeds texts with
/// the real OnnxEmbeddingProvider (optionally through a JSON cache keyed by embedding space and text).
/// </summary>
/// <param name="report">Receives progress lines</param>
/// <param name="texts">Texts to embed with EmbedQueryAsync</param>

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Infrastructure.Onnx;
using NUnit.Framework;

namespace Klacks.IntegrationTest.KnowledgeIndex.TurnIntent;

public static class TurnIntentEmbeddingEnvironment
{
    public static void LoadLanguagePlugins(StringBuilder report)
    {
        var apiDirectory = ResolvePluginRoot();
        MutationIntentPluginLoader.Load(
            apiDirectory,
            (file, ex) => report.AppendLine($"Plugin file failed to load: {file} ({ex.Message})"));
        report.AppendLine($"Language plugins loaded from: {apiDirectory}");
    }

    public static string ResolveApiSourceDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, TurnIntentProbeSettings.ApiProjectDirectory);
            if (File.Exists(Path.Combine(candidate, TurnIntentProbeSettings.ApiProjectFile)))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The Klacks.Api source directory was not found above the test output.");
    }

    public static async Task<(float[][] Vectors, string EmbeddingSpaceId)> EmbedAsync(
        IReadOnlyList<string> texts, StringBuilder report)
    {
        var cachePath = Environment.GetEnvironmentVariable(TurnIntentProbeSettings.EmbeddingCacheEnvironmentVariable);
        var cache = LoadCache(cachePath);

        await using var provider = new OnnxEmbeddingProvider(
            new ModelLoader(new HttpClient()), ResolveEmbeddingDirectory());

        report.AppendLine($"Embedding space: {provider.EmbeddingSpaceId}; dimension {provider.Dimension}");

        var vectors = new float[texts.Count][];
        var computed = 0;
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < texts.Count; i++)
        {
            var key = provider.EmbeddingSpaceId + "|" + texts[i];
            if (!cache.TryGetValue(key, out var vector))
            {
                vector = await provider.EmbedQueryAsync(texts[i], CancellationToken.None);
                cache[key] = vector;
                computed++;
            }

            vectors[i] = vector;
        }

        stopwatch.Stop();
        report.AppendLine(
            $"Embedded {computed} texts (the rest from cache) in {stopwatch.Elapsed.TotalSeconds:F1} s" +
            (computed > 0 ? $" = {stopwatch.Elapsed.TotalMilliseconds / computed:F0} ms per text" : string.Empty));

        SaveCache(cachePath, cache);
        return (vectors, provider.EmbeddingSpaceId);
    }

    private static string ResolvePluginRoot() =>
        Directory.Exists(Path.Combine(AppContext.BaseDirectory, TurnIntentProbeSettings.PluginsRelativePath))
            ? AppContext.BaseDirectory
            : ResolveApiSourceDirectory();

    private static string ResolveEmbeddingDirectory()
    {
        var configuredRoot = Environment.GetEnvironmentVariable(TurnIntentProbeSettings.ModelsRootEnvironmentVariable);
        var root = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(AppContext.BaseDirectory, KnowledgeIndexConstants.ModelsCacheSubdirectory)
            : configuredRoot;
        var directory = Path.Combine(root, KnowledgeIndexConstants.EmbeddingModelName);

        var modelPresent = File.Exists(Path.Combine(directory, KnowledgeIndexConstants.EmbeddingModelFileName));
        var tokenizerPresent = File.Exists(Path.Combine(directory, KnowledgeIndexConstants.EmbeddingTokenizerFileName));
        if (!modelPresent || !tokenizerPresent)
        {
            Assert.Inconclusive(
                $"{KnowledgeIndexConstants.EmbeddingModelFileName} and {KnowledgeIndexConstants.EmbeddingTokenizerFileName} " +
                $"are missing in '{directory}'; set {TurnIntentProbeSettings.ModelsRootEnvironmentVariable} to a models root " +
                "that holds them (the loader would otherwise download ~555 MB).");
        }

        return directory;
    }

    private static Dictionary<string, float[]> LoadCache(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new Dictionary<string, float[]>(StringComparer.Ordinal);
        }

        return JsonSerializer.Deserialize<Dictionary<string, float[]>>(File.ReadAllText(path))
               ?? new Dictionary<string, float[]>(StringComparer.Ordinal);
    }

    private static void SaveCache(string? path, Dictionary<string, float[]> cache)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            File.WriteAllText(path, JsonSerializer.Serialize(cache));
        }
    }
}
