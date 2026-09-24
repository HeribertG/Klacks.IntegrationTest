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
        var path = Path.Combine(AppContext.BaseDirectory, "Inbound", "Goldsets", "inbound-clarification-v1.json");

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
              { "id": "bad-shape", "forbiddenDates": "2030-01-01" }
            ] }
            """;

        var problems = InboundGoldsetSchemaValidator.Validate(Json);

        problems.Count.ShouldBe(6);
        problems.ShouldContain(problem => problem.StartsWith("bad-intent:", StringComparison.Ordinal));
        problems.ShouldContain(problem => problem.StartsWith("bad-numeric-intent:", StringComparison.Ordinal));
        problems.ShouldContain(problem => problem.StartsWith("bad-confidence:", StringComparison.Ordinal));
        problems.Count(problem => problem.StartsWith("bad-date:", StringComparison.Ordinal)).ShouldBe(2);
        problems.ShouldContain(problem => problem.StartsWith("bad-shape:", StringComparison.Ordinal));
        problems.ShouldNotContain(problem => problem.StartsWith("ok-1:", StringComparison.Ordinal));
    }
}
