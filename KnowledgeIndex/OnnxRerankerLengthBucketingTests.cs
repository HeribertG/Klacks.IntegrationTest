// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards the length bucketing in <see cref="OnnxRerankerProvider"/>. The provider reorders candidates
/// by token length before batching them, so a score now travels a different route back to its candidate
/// than the index it arrived at. If that mapping ever slips, every candidate silently receives another
/// candidate's score — retrieval would keep working and keep returning plausible, wrong rankings, which
/// no recall report would attribute to the reranker. These tests pin the invariant that a score belongs
/// to its candidate regardless of input order and regardless of how the batches happen to fall.
/// </summary>

using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Infrastructure.Onnx;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.KnowledgeIndex;

[TestFixture]
[Explicit("Needs the cached ONNX reranker model. Run manually.")]
[Category("RealDatabase")]
public class OnnxRerankerLengthBucketingTests
{
    private const string Query = "Wie lege ich einen neuen Mitarbeiter an?";
    private const double Tolerance = 1e-6;

    private OnnxRerankerProvider _provider = null!;

    private static readonly string[] Candidates =
    {
        "create_employee. Legt einen neuen Mitarbeiter an.",
        BuildLong("list_contracts. Listet alle Vertraege auf. ", 3200),
        "open_schedule. Oeffnet den Dienstplan.",
        BuildLong("search_shifts. Sucht Schichten nach Zeitraum. ", 1400),
        "delete_group. Loescht eine Gruppe.",
        BuildLong("update_client. Aendert Stammdaten eines Klienten. ", 2400),
        "list_absence_types. Listet die Abwesenheitsarten auf.",
        "add_expense. Erfasst eine Spesenposition.",
        BuildLong("explain_page_inbox. Erklaert die Posteingangsseite. ", 900),
        "cut_shift. Teilt eine Schicht.",
    };

    private static string BuildLong(string seed, int length)
    {
        var text = string.Concat(Enumerable.Repeat(seed, length / seed.Length + 1));
        return text[..length];
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("KnowledgeIndex__ModelsRoot"),
            Path.Combine(Path.GetTempPath(), "klacks-test-models"),
            Path.Combine(localAppData, "Klacks", "models"),
            Path.Combine(localAppData, "Klacks", KnowledgeIndexConstants.ModelsCacheSubdirectory),
            Path.Combine(localAppData, KnowledgeIndexConstants.ModelsCacheSubdirectory),
        };

        var dir = roots
            .Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r))
            .Select(r => Path.Combine(r!, KnowledgeIndexConstants.RerankerModelName))
            .FirstOrDefault(d => File.Exists(Path.Combine(d, KnowledgeIndexConstants.RerankerModelFileName)));

        if (dir is null)
            Assert.Ignore("Reranker model not found in any known cache root. Set KnowledgeIndex__ModelsRoot.");

        _provider = new OnnxRerankerProvider(new ModelLoader(new HttpClient()), dir!, string.Empty, string.Empty, string.Empty, string.Empty);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }
    }

    [Test]
    public async Task ScoreAsync_ReorderedInput_MovesEachScoreWithItsCandidate()
    {
        var baseline = await _provider.ScoreAsync(Query, Candidates, CancellationToken.None);

        var permutation = new[] { 4, 1, 9, 0, 6, 3, 8, 2, 7, 5 };
        var shuffled = permutation.Select(i => Candidates[i]).ToList();

        var shuffledScores = await _provider.ScoreAsync(Query, shuffled, CancellationToken.None);

        for (var i = 0; i < permutation.Length; i++)
        {
            shuffledScores[i].ShouldBe(
                baseline[permutation[i]],
                Tolerance,
                $"candidate '{Truncate(shuffled[i])}' scored {shuffledScores[i]} at position {i} " +
                $"but {baseline[permutation[i]]} at position {permutation[i]} — the bucketing lost track of it");
        }
    }

    [Test]
    public async Task ScoreAsync_SingleCandidate_MatchesItsScoreInTheFullBatch()
    {
        var full = await _provider.ScoreAsync(Query, Candidates, CancellationToken.None);

        for (var i = 0; i < Candidates.Length; i++)
        {
            var alone = await _provider.ScoreAsync(Query, [Candidates[i]], CancellationToken.None);

            alone[0].ShouldBe(
                full[i],
                1e-3,
                $"candidate '{Truncate(Candidates[i])}' scored differently alone than inside the batch — " +
                "padding must not influence a score");
        }
    }

    [Test]
    public async Task ScoreAsync_RelevantCandidate_OutranksUnrelatedOnes()
    {
        var scores = await _provider.ScoreAsync(Query, Candidates, CancellationToken.None);

        var best = Array.IndexOf(scores, scores.Max());

        Candidates[best].ShouldStartWith(
            "create_employee",
            Case.Sensitive,
            "the candidate that answers the query should rank first; a different winner means scores " +
            "and candidates are misaligned");
    }

    private static string Truncate(string value) =>
        value.Length <= 40 ? value : value[..40];
}
