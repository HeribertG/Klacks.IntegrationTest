// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Checks the closed-vocabulary fields of the inbound clarification goldset file before any LLM call is made,
/// for the message items and the answer items: expectIntent and forbiddenIntent must name EmailIntent values,
/// maxConfidence an EmailConfidence value, forbiddenDates (forged "today" dates that must not end up in the
/// analysed period) and expectFromDate must be yyyy-MM-dd dates. An unknown name would otherwise never match and silently turn a hard check into
/// a no-op in the middle of a run that costs money. Every problem is reported with its item id.
/// </summary>
/// <param name="json">The goldset file content</param>

using System.Globalization;
using System.Text.Json;
using Klacks.Api.Domain.Enums;

namespace Klacks.IntegrationTest.Inbound;

internal static class InboundGoldsetSchemaValidator
{
    internal const string DateFormat = "yyyy-MM-dd";

    private const string ItemsProperty = "items";
    private const string AnswerItemsProperty = "answerItems";
    private const string ExpectIntentProperty = "expectIntent";
    private const string ExpectFromDateProperty = "expectFromDate";
    private const string IdProperty = "id";
    private const string ForbiddenIntentProperty = "forbiddenIntent";
    private const string MaxConfidenceProperty = "maxConfidence";
    private const string ForbiddenDatesProperty = "forbiddenDates";
    private const string UnknownId = "(no id)";

    internal static List<string> Validate(string json)
    {
        var problems = new List<string>();
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty(ItemsProperty, out var items) || items.ValueKind != JsonValueKind.Array)
        {
            problems.Add($"the file has no '{ItemsProperty}' array");
            return problems;
        }

        foreach (var item in items.EnumerateArray())
        {
            var id = IdOf(item);
            ValidateNames(id, item, ExpectIntentProperty, typeof(EmailIntent), problems);
            ValidateNames(id, item, ForbiddenIntentProperty, typeof(EmailIntent), problems);
            ValidateNames(id, item, MaxConfidenceProperty, typeof(EmailConfidence), problems);
            ValidateDates(id, item, problems);
            ValidateExpectedDate(id, item, problems);
        }

        if (document.RootElement.TryGetProperty(AnswerItemsProperty, out var answerItems) && answerItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in answerItems.EnumerateArray())
            {
                ValidateNames(IdOf(item), item, ExpectIntentProperty, typeof(EmailIntent), problems);
            }
        }

        return problems;
    }

    private static string IdOf(JsonElement item) =>
        item.TryGetProperty(IdProperty, out var idElement) ? idElement.GetString() ?? UnknownId : UnknownId;

    private static void ValidateExpectedDate(string id, JsonElement item, List<string> problems)
    {
        if (!item.TryGetProperty(ExpectFromDateProperty, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        var text = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        if (text == null || !DateOnly.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            problems.Add($"{id}: {ExpectFromDateProperty} value '{element}' is not a {DateFormat} date");
        }
    }

    private static void ValidateNames(string id, JsonElement item, string property, Type enumType, List<string> problems)
    {
        if (!item.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        var values = element.ValueKind == JsonValueKind.Array ? element.EnumerateArray().ToList() : [element];
        foreach (var value in values)
        {
            var name = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (name == null || !Enum.GetNames(enumType).Contains(name, StringComparer.Ordinal))
            {
                problems.Add($"{id}: {property} value '{value}' is not a {enumType.Name} name");
            }
        }
    }

    private static void ValidateDates(string id, JsonElement item, List<string> problems)
    {
        if (!item.TryGetProperty(ForbiddenDatesProperty, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            problems.Add($"{id}: {ForbiddenDatesProperty} must be an array of {DateFormat} dates");
            return;
        }

        foreach (var value in element.EnumerateArray())
        {
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (text == null || !DateOnly.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                problems.Add($"{id}: {ForbiddenDatesProperty} value '{value}' is not a {DateFormat} date");
            }
        }
    }
}
