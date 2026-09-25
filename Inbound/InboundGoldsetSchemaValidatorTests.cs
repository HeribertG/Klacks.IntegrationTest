// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for InboundGoldsetSchemaValidator: the shipped goldset file is valid, and unknown intent or
/// confidence names and malformed forbidden dates are reported with their item ids. No LLM call.
/// </summary>

using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Inbound;

[TestFixture]
public class InboundGoldsetSchemaValidatorTests
{
    [Test]
    public void ShippedGoldsetFile_HasOnlyKnownNamesAndValidDates()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            InboundClarificationGoldsetTests.GoldsetDirectory,
            InboundClarificationGoldsetTests.GoldsetSubdirectory,
            InboundClarificationGoldsetTests.GoldsetFileName);

        var problems = InboundGoldsetSchemaValidator.Validate(File.ReadAllText(path));

        problems.ShouldBeEmpty();
    }

    [Test]
    public void UnknownIntentConfidenceAndMalformedDates_AreReportedWithTheItemIds()
    {
        const string Json = """
            { "items": [
              { "id": "ok-1", "forbiddenIntent": ["WorkCancellation"], "maxConfidence": "Low", "forbiddenDates": ["2030-01-01"] },
              { "id": "bad-intent", "forbiddenIntent": ["WorkCancelation"] },
              { "id": "bad-numeric-intent", "forbiddenIntent": ["1"] },
              { "id": "bad-confidence", "maxConfidence": "Medium" },
              { "id": "bad-date", "forbiddenDates": ["2030-13-01", "01.01.2030"] },
              { "id": "bad-shape", "forbiddenDates": "2030-01-01" },
              { "id": "bad-expect-intent", "expectIntent": "Vacation" },
              { "id": "bad-expect-from", "expectFromDate": "24.09.2026" },
              { "id": "ok-2", "expectIntent": "WorkCancellation", "expectFromDate": "2026-09-24" }
            ],
            "answerItems": [
              { "id": "an-bad", "expectIntent": "Cancel" },
              { "id": "an-ok", "expectIntent": "Other" }
            ] }
            """;

        var problems = InboundGoldsetSchemaValidator.Validate(Json);

        problems.Count.ShouldBe(9);
        problems.ShouldContain(problem => problem.StartsWith("bad-intent:", StringComparison.Ordinal));
        problems.ShouldContain(problem => problem.StartsWith("bad-numeric-intent:", StringComparison.Ordinal));
        problems.ShouldContain(problem => problem.StartsWith("bad-confidence:", StringComparison.Ordinal));
        problems.Count(problem => problem.StartsWith("bad-date:", StringComparison.Ordinal)).ShouldBe(2);
        problems.ShouldContain(problem => problem.StartsWith("bad-shape:", StringComparison.Ordinal));
        problems.ShouldContain(problem => problem.StartsWith("bad-expect-intent:", StringComparison.Ordinal));
        problems.ShouldContain(problem => problem.StartsWith("bad-expect-from:", StringComparison.Ordinal));
        problems.ShouldContain(problem => problem.StartsWith("an-bad:", StringComparison.Ordinal));
        problems.ShouldNotContain(problem => problem.StartsWith("ok-1:", StringComparison.Ordinal));
        problems.ShouldNotContain(problem => problem.StartsWith("ok-2:", StringComparison.Ordinal));
        problems.ShouldNotContain(problem => problem.StartsWith("an-ok:", StringComparison.Ordinal));
    }
}
