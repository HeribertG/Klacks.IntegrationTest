// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Platform probe for the local ONNX stack. ONNX is disabled on Windows ARM64 because ONNX Runtime
/// 1.20.1 faulted the process there; the project now ships 1.27.1, so the block may be obsolete. This
/// opens an InferenceSession on the real model files and reports whether the runtime survives it —
/// run it on any new platform (ARM servers in particular) before trusting the fallback switch.
/// Explicit: it loads model files from the build output and is a diagnostic, not a regression gate.
/// </summary>

using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.KnowledgeIndex;

[TestFixture]
[Explicit("Diagnostic probe: run manually to check ONNX Runtime support on the current platform.")]
public class OnnxRuntimePlatformProbeTests
{
    private const string EmbeddingModelDirectory = "multilingual-e5-small";
    private const string RerankerModelDirectory = "mmarco-mMiniLMv2-L12-H384-v1";
    private const string ModelFileName = "model.onnx";

    [TestCase(EmbeddingModelDirectory)]
    [TestCase(RerankerModelDirectory)]
    public void InferenceSession_OpensOnThisPlatform(string modelDirectory)
    {
        TestContext.WriteLine($"OS={RuntimeInformation.OSDescription}");
        TestContext.WriteLine($"Architecture={RuntimeInformation.ProcessArchitecture}");

        var path = Path.Combine(AppContext.BaseDirectory, "Cache", "Models", modelDirectory, ModelFileName);
        if (!File.Exists(path))
        {
            Assert.Ignore($"Model not present at {path} — nothing to probe.");
        }

        using var session = new InferenceSession(path);

        TestContext.WriteLine($"{modelDirectory}: session opened.");
        TestContext.WriteLine($"  inputs:  {string.Join(", ", session.InputMetadata.Keys)}");
        TestContext.WriteLine($"  outputs: {string.Join(", ", session.OutputMetadata.Keys)}");

        session.InputMetadata.ShouldNotBeEmpty();

        // Opening a session is not the same as running one: the original Snapdragon fault happened in
        // the runtime's CPU feature detection, which a real forward pass exercises far more than model
        // loading does. Run a minimal batch so the probe answers the question that actually matters.
        var tokenCount = 4;
        var ids = new DenseTensor<long>(new[] { 1, tokenCount });
        var mask = new DenseTensor<long>(new[] { 1, tokenCount });
        var types = new DenseTensor<long>(new[] { 1, tokenCount });
        for (var i = 0; i < tokenCount; i++)
        {
            ids[0, i] = i + 1;
            mask[0, i] = 1;
            types[0, i] = 0;
        }

        var inputs = new List<NamedOnnxValue>();
        foreach (var name in session.InputMetadata.Keys)
        {
            var tensor = name switch
            {
                "attention_mask" => mask,
                "token_type_ids" => types,
                _ => ids,
            };
            inputs.Add(NamedOnnxValue.CreateFromTensor(name, tensor));
        }

        using var results = session.Run(inputs);
        var first = results.First();
        var shape = first.AsTensor<float>().Dimensions.ToArray();

        TestContext.WriteLine($"  forward pass OK -> {first.Name} {string.Join("x", shape)}");
        shape.Length.ShouldBeGreaterThan(0);
    }
}
