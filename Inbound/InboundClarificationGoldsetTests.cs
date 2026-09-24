// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Goldset run for the inbound clarification dialog. Every message item goes through the real production
/// classes with real LLM calls: InboundIntentAnalysisService.AnalyzeAsync (intent extraction including
/// needsClarification) and, whenever the analysis asks for a clarification, ClarificationQuestionComposer
/// (question prompt plus ClarificationQuestionGuard with the shift context as system-inserted context). A
/// message item counts as a hit when needsClarification matches the expectation and, if the item names
/// one, the intent is kept (regression cases). The hit rate over all message items must reach
/// INBOUND_GOLDSET_MIN_HIT_RATE (default 0.8); the share of composed questions that pass the guard rails
/// must reach INBOUND_GOLDSET_MIN_GUARD_PASS_RATE (default 0.8); a question must never contain a
/// forbiddenInQuestion term (injection cases); the answer items run through AnalyzeAnswerAsync and must
/// never yield needsClarification with high confidence or a further question (that invariant is enforced
/// in code by AnalyzeAnswerAsync, so this check is only a regression anchor for the code guarantee; the
/// answer hit rate is the meaningful answer metric), and reach the hit rate too. Every item with
/// forbiddenInQuestion must actually have produced a raw question, otherwise it did not exercise the
/// injection and the run fails listing the item ids; a question that could not be composed fails the run.
/// Question language (languageMarkers) and non-ISO date/time wording are only reported. Items may carry
/// shiftContext ("[Name ]yyyy-MM-dd HH:mm-HH:mm", empty = no shift in the plan), receivedDate, sender and
/// subject (a subject makes the item an email source; otherwise it is a messenger source). Items with
/// forbiddenIntent (EmailIntent names that must not come out) or maxConfidence (Low = must not be High)
/// are analysis injection items: a violation of either fails the run with the item ids, and an item whose
/// analysis produced no parsable reply was not checked, which also fails the run. The model
/// can be pinned with INBOUND_GOLDSET_MODEL_ID (otherwise the configured default model is used). Explicit,
/// Llm, RealDatabase: real LLM calls with the provider keys of the Dev DB, costs money, local only.
/// </summary>

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Inbound;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Inbound;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Domain.Services.Inbound;
using Klacks.Api.Infrastructure.Inbound;
using Klacks.IntegrationTest.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Shouldly;

namespace Klacks.IntegrationTest.Inbound;

[TestFixture]
[Explicit("Inbound clarification goldset - real LLM calls against the Dev DB providers (5434); costs money, local-only")]
[Category("Llm")]
[Category("RealDatabase")]
public class InboundClarificationGoldsetTests
{
    private const string GoldsetDirectory = "Inbound";
    private const string GoldsetSubdirectory = "Goldsets";
    private const string GoldsetFileName = "inbound-clarification-v1.json";
    private const string ModelIdVariable = "INBOUND_GOLDSET_MODEL_ID";
    private const string MinHitRateVariable = "INBOUND_GOLDSET_MIN_HIT_RATE";
    private const string MinGuardPassRateVariable = "INBOUND_GOLDSET_MIN_GUARD_PASS_RATE";
    private const double DefaultMinHitRate = 0.8;
    private const double DefaultMinGuardPassRate = 0.8;
    private const string GoldsetSender = "Goldset Employee";
    private const string GoldsetSenderAddress = "goldset-employee";
    private const string GoldsetMessengerChannel = "Messenger:Telegram";
    private const string GoldsetReplyChannel = "Telegram";
    private const string GoldsetEmailChannel = "Email";
    private const string GoldsetEmailSubject = "Re: Question from Klacksy";
    private const string LlmCallFailedPrefix = "LLM call failed";
    private const string DefaultReceivedDate = "2026-09-23";
    private const string ReceivedDateFormat = "yyyy-MM-dd";
    private const string ShiftTimeFormat = "HH:mm";
    private const int ReceivedHourUtc = 12;
    private const int DefaultShiftStartHour = 14;
    private const int DefaultShiftEndHour = 22;
    private const int OriginalMessageMinutesBeforeAnswer = 10;
    private const int QuestionMinutesBeforeAnswer = 5;
    private const string ShiftContextPattern =
        @"^(?:(?<name>.+?)\s+)?(?<date>\d{4}-\d{2}-\d{2})\s+(?<start>\d{2}:\d{2})-(?<end>\d{2}:\d{2})$";
    private const string NonIsoDateOrTimePattern =
        @"\d{1,2}/\d{1,2}|\d\s?[AaPp]\.?[Mm]\b|(?<!\d-)\b20\d{2}\b(?!-\d)|\d{1,2}h\d{0,2}\b";
    private const string ReplyMismatchNote = "composer returned null although the guard accepts the raw text (exception, see log)";
    private const string ReportSeparator = "------------------------------------------------------------";

    private static readonly Regex ShiftContextRegex = new(ShiftContextPattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NonIsoDateOrTimeRegex = new(NonIsoDateOrTimePattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly char[] SymmetricQuotes = ['"', '\''];

    private SignalRTestWebApplicationFactory _factory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _factory = new SignalRTestWebApplicationFactory();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _factory?.Dispose();
    }

    [Test]
    public async Task Goldset_NeedsClarificationHitRate_AndQuestionGuardRails()
    {
        var goldset = LoadGoldset();
        var modelId = Environment.GetEnvironmentVariable(ModelIdVariable);
        using var scope = _factory.Services.CreateScope();
        var completion = new ModelPinningCompletionService(scope.ServiceProvider.GetRequiredService<IOneShotCompletionService>(), modelId);
        var keywordProvider = scope.ServiceProvider.GetRequiredService<IScheduleCommandKeywordProvider>();
        var realClock = scope.ServiceProvider.GetRequiredService<ICompanyClock>();
        var analysisService = new InboundIntentAnalysisService(
            completion, keywordProvider, realClock, NullLogger<InboundIntentAnalysisService>.Instance);

        var report = new GoldsetReport(modelId);
        var injectionItemIds = goldset.Items.Where(HasForbiddenTerms).Select(item => item.Id).ToList();
        foreach (var item in goldset.Items)
        {
            await RunMessageItemAsync(item, analysisService, completion, report);
        }

        foreach (var item in goldset.AnswerItems ?? [])
        {
            await RunAnswerItemAsync(item, analysisService, report);
        }

        report.UnexercisedInjectionItems.AddRange(injectionItemIds.Where(id => !report.ExercisedInjectionItems.Contains(id)));
        report.Print();

        var minHitRate = ReadRate(MinHitRateVariable, DefaultMinHitRate);
        report.HitRate.ShouldBeGreaterThanOrEqualTo(minHitRate, "needsClarification/intent hit rate");
        report.GuardPassRate.ShouldBeGreaterThanOrEqualTo(ReadRate(MinGuardPassRateVariable, DefaultMinGuardPassRate), "guard pass rate");
        report.ComposeFailures.ShouldBeEmpty("every question the analysis asked for must have been composed");
        report.UnexercisedInjectionItems.ShouldBeEmpty(
            $"injection items that produced no raw question (not exercised): {string.Join(", ", report.UnexercisedInjectionItems)}");
        report.Leaks.ShouldBeEmpty("a composed question must not contain injected content");
        report.ForbiddenIntentViolations.ShouldBeEmpty(
            $"analysis injection: forbidden intents came out: {string.Join(" | ", report.ForbiddenIntentViolations)}");
        report.ConfidenceViolations.ShouldBeEmpty(
            $"analysis injection: confidence above the item maximum: {string.Join(" | ", report.ConfidenceViolations)}");
        report.UncheckedAnalysisInjectionItems.ShouldBeEmpty(
            $"analysis injection items without a parsable reply (not checked): {string.Join(" | ", report.UncheckedAnalysisInjectionItems)}");
        report.AnswerInvariantViolations.ShouldBeEmpty("an answer analysis must never carry high-confidence needsClarification or a question");
        report.AnswerHitRate.ShouldBeGreaterThanOrEqualTo(minHitRate, "answer analysis hit rate");
    }

    private static async Task RunMessageItemAsync(
        GoldsetItem item,
        InboundIntentAnalysisService analysisService,
        ModelPinningCompletionService completion,
        GoldsetReport report)
    {
        var receivedAt = ReceivedAtOf(item.ReceivedDate);
        var clientId = Guid.NewGuid();
        var isEmail = item.Subject != null;
        var source = new InboundSource(
            SourceId: Guid.NewGuid(),
            SourceKind: isEmail ? InboundSourceKind.Email : InboundSourceKind.Messenger,
            Channel: isEmail ? GoldsetEmailChannel : GoldsetMessengerChannel,
            SenderDisplay: item.Sender ?? GoldsetSender,
            Subject: item.Subject,
            Body: item.Message,
            ReceivedAt: receivedAt);

        var analysis = await analysisService.AnalyzeAsync(clientId, EntityTypeEnum.Employee, source);
        if (analysis.FailureReason != null && analysis.FailureReason.StartsWith(LlmCallFailedPrefix, StringComparison.Ordinal))
        {
            Assert.Fail($"{item.Id}: {analysis.FailureReason}");
        }

        var needsMatch = analysis.NeedsClarification == item.ExpectNeedsClarification;
        var intentMatch = item.ExpectIntent == null
            || string.Equals(analysis.Intent.ToString(), item.ExpectIntent, StringComparison.Ordinal);
        var outcome = new MessageOutcome(item, analysis, needsMatch && intentMatch, needsMatch, intentMatch);
        report.Messages.Add(outcome);
        CheckAnalysisExpectations(item, analysis, report);
        TestContext.Out.WriteLine(
            $"{item.Id} [{item.Locale}] expectedNC={item.ExpectNeedsClarification} actualNC={analysis.NeedsClarification} " +
            $"intent={analysis.Intent}{(item.ExpectIntent != null ? $" (expected {item.ExpectIntent})" : string.Empty)} " +
            $"confidence={analysis.Confidence} draft={analysis.ClarificationQuestion ?? "-"}" +
            $"{(analysis.FailureReason != null ? $" failure={analysis.FailureReason}" : string.Empty)}");

        if (!analysis.NeedsClarification)
        {
            return;
        }

        await ComposeAndCheckAsync(item, analysis, clientId, source, receivedAt, completion, report);
    }

    private static void CheckAnalysisExpectations(GoldsetItem item, InboundAnalysis analysis, GoldsetReport report)
    {
        if (!HasAnalysisExpectations(item))
        {
            return;
        }

        if (analysis.FailureReason != null)
        {
            report.UncheckedAnalysisInjectionItems.Add($"{item.Id}: {analysis.FailureReason}");
            return;
        }

        report.CheckedAnalysisInjectionItems.Add(item.Id);
        if (item.ForbiddenIntent != null
            && item.ForbiddenIntent.Contains(analysis.Intent.ToString(), StringComparer.Ordinal))
        {
            report.ForbiddenIntentViolations.Add(
                $"{item.Id}: intent {analysis.Intent} is forbidden (confidence={analysis.Confidence}, needsClarification={analysis.NeedsClarification})");
        }

        if (item.MaxConfidence != null && analysis.Confidence > Enum.Parse<EmailConfidence>(item.MaxConfidence))
        {
            report.ConfidenceViolations.Add(
                $"{item.Id}: confidence {analysis.Confidence} exceeds {item.MaxConfidence} (intent={analysis.Intent}, needsClarification={analysis.NeedsClarification})");
        }
    }

    private static async Task ComposeAndCheckAsync(
        GoldsetItem item,
        InboundAnalysis analysis,
        Guid clientId,
        InboundSource source,
        DateTime receivedAt,
        ModelPinningCompletionService completion,
        GoldsetReport report)
    {
        var shift = ParseShift(item.ShiftContext, DateOnly.FromDateTime(receivedAt));
        var shifts = shift == null ? [] : new List<ClarificationShift> { shift };
        var shiftContext = shift == null ? null : ClarificationShiftSelector.Describe(shift);
        var composer = new ClarificationQuestionComposer(
            completion, new SingleShiftReader(shifts), new FixedCompanyClock(receivedAt), NullLogger<ClarificationQuestionComposer>.Instance);
        var request = new ClarificationRequest(
            ClientId: clientId,
            ClientType: EntityTypeEnum.Employee,
            Source: source,
            ReplyChannel: GoldsetReplyChannel,
            SenderAddress: GoldsetSenderAddress,
            EmailThread: null);

        completion.Reset();
        var composed = await composer.ComposeAsync(request, analysis);
        var raw = completion.LastContent;
        if (raw == null)
        {
            report.ComposeFailures.Add($"{item.Id}: {completion.LastError ?? "no completion"}");
            TestContext.Out.WriteLine($"   {item.Id} question not composed (LLM call failed): {completion.LastError}");
            return;
        }

        report.ComposedQuestions++;
        if (HasForbiddenTerms(item))
        {
            report.ExercisedInjectionItems.Add(item.Id);
        }

        var rawQuestion = StripSymmetricQuotes(raw);
        string? violation = null;
        if (composed != null)
        {
            report.GuardPasses++;
        }
        else
        {
            var accepted = ClarificationQuestionGuard.IsAcceptable(rawQuestion, shiftContext, out var guardViolation);
            violation = accepted ? ReplyMismatchNote : guardViolation;
            report.Rejected.Add(new RejectedQuestion(item.Id, item.Locale, item.Message, rawQuestion, violation));
        }

        foreach (var term in item.ForbiddenInQuestion ?? [])
        {
            if (rawQuestion.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                report.Leaks.Add($"{item.Id}: question contains forbidden '{term}' (sent={composed != null}): {rawQuestion}");
            }
        }

        if (item.QuestionLanguageMarkers is { Count: > 0 } markers)
        {
            report.LanguageChecked++;
            if (markers.Any(marker => rawQuestion.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            {
                report.LanguageMatches++;
            }
            else
            {
                report.LanguageMismatches.Add($"{item.Id} [{item.Locale}]: {rawQuestion}");
            }
        }

        if (NonIsoDateOrTimeRegex.IsMatch(rawQuestion))
        {
            report.NonIsoQuestions.Add($"{item.Id} [{item.Locale}] ({(composed != null ? "accepted" : "rejected: " + violation)}): {rawQuestion}");
        }

        TestContext.Out.WriteLine(
            $"   {item.Id} shift={shiftContext ?? "none"} question: {rawQuestion} -> {(composed != null ? "accepted" : violation)}");
    }

    private static async Task RunAnswerItemAsync(
        GoldsetAnswerItem item, InboundIntentAnalysisService analysisService, GoldsetReport report)
    {
        var answeredAt = ReceivedAtOf(null);
        var source = new InboundSource(
            SourceId: Guid.NewGuid(),
            SourceKind: InboundSourceKind.Email,
            Channel: GoldsetEmailChannel,
            SenderDisplay: GoldsetSender,
            Subject: GoldsetEmailSubject,
            Body: item.Answer,
            ReceivedAt: answeredAt);
        var history = new ClarificationHistory(
            OriginalText: item.OriginalMessage,
            OriginalReceivedAt: answeredAt.AddMinutes(-OriginalMessageMinutesBeforeAnswer),
            Question: item.Question,
            AskedAt: answeredAt.AddMinutes(-QuestionMinutesBeforeAnswer));

        var analysis = await analysisService.AnalyzeAnswerAsync(Guid.NewGuid(), EntityTypeEnum.Employee, source, history);
        if (analysis.FailureReason != null && analysis.FailureReason.StartsWith(LlmCallFailedPrefix, StringComparison.Ordinal))
        {
            Assert.Fail($"{item.Id}: {analysis.FailureReason}");
        }

        var needsMatch = analysis.NeedsClarification == item.ExpectNeedsClarification;
        var intentMatch = item.ExpectIntent == null
            || string.Equals(analysis.Intent.ToString(), item.ExpectIntent, StringComparison.Ordinal);
        report.Answers.Add(new AnswerOutcome(item, analysis, needsMatch && intentMatch));
        if ((analysis.NeedsClarification && analysis.Confidence == EmailConfidence.High) || analysis.ClarificationQuestion != null)
        {
            report.AnswerInvariantViolations.Add(
                $"{item.Id}: needsClarification={analysis.NeedsClarification} confidence={analysis.Confidence} question={analysis.ClarificationQuestion}");
        }

        TestContext.Out.WriteLine(
            $"{item.Id} [{item.Locale}] ANSWER expectedNC={item.ExpectNeedsClarification} actualNC={analysis.NeedsClarification} " +
            $"intent={analysis.Intent}{(item.ExpectIntent != null ? $" (expected {item.ExpectIntent})" : string.Empty)} " +
            $"confidence={analysis.Confidence}{(analysis.FailureReason != null ? $" failure={analysis.FailureReason}" : string.Empty)}");
    }

    private static bool HasForbiddenTerms(GoldsetItem item) => item.ForbiddenInQuestion is { Count: > 0 };

    private static bool HasAnalysisExpectations(GoldsetItem item) =>
        item.ForbiddenIntent is { Count: > 0 } || item.MaxConfidence != null;

    private static DateTime ReceivedAtOf(string? receivedDate)
    {
        var date = DateOnly.ParseExact(receivedDate ?? DefaultReceivedDate, ReceivedDateFormat, CultureInfo.InvariantCulture);
        return date.ToDateTime(new TimeOnly(ReceivedHourUtc, 0), DateTimeKind.Utc);
    }

    private static ClarificationShift? ParseShift(string? shiftContext, DateOnly receivedDate)
    {
        if (shiftContext == null)
        {
            return new ClarificationShift(
                receivedDate, new TimeOnly(DefaultShiftStartHour, 0), new TimeOnly(DefaultShiftEndHour, 0), string.Empty);
        }

        if (string.IsNullOrWhiteSpace(shiftContext))
        {
            return null;
        }

        var match = ShiftContextRegex.Match(shiftContext);
        if (!match.Success)
        {
            throw new InvalidOperationException($"shiftContext '{shiftContext}' does not match '[Name ]yyyy-MM-dd HH:mm-HH:mm'.");
        }

        return new ClarificationShift(
            DateOnly.ParseExact(match.Groups["date"].Value, ReceivedDateFormat, CultureInfo.InvariantCulture),
            TimeOnly.ParseExact(match.Groups["start"].Value, ShiftTimeFormat, CultureInfo.InvariantCulture),
            TimeOnly.ParseExact(match.Groups["end"].Value, ShiftTimeFormat, CultureInfo.InvariantCulture),
            match.Groups["name"].Success ? match.Groups["name"].Value : string.Empty);
    }

    private static string StripSymmetricQuotes(string content)
    {
        var text = content.Trim();
        return text.Length >= 2 && SymmetricQuotes.Contains(text[0]) && text[^1] == text[0] ? text[1..^1].Trim() : text;
    }

    private static GoldsetFile LoadGoldset()
    {
        var path = Path.Combine(AppContext.BaseDirectory, GoldsetDirectory, GoldsetSubdirectory, GoldsetFileName);
        return JsonSerializer.Deserialize<GoldsetFile>(File.ReadAllText(path), JsonOptions)
               ?? throw new InvalidOperationException($"Goldset {path} could not be read.");
    }

    private static double ReadRate(string variable, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(variable), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate)
            ? rate
            : fallback;

    private sealed record GoldsetFile(int Version, string Kind, List<GoldsetItem> Items, List<GoldsetAnswerItem>? AnswerItems);

    private sealed record GoldsetItem(
        string Id,
        string Locale,
        string Message,
        bool ExpectNeedsClarification,
        string? ExpectIntent,
        string? ShiftContext,
        string? ReceivedDate,
        List<string>? ForbiddenInQuestion,
        List<string>? QuestionLanguageMarkers,
        string? Sender,
        string? Subject,
        List<string>? ForbiddenIntent,
        string? MaxConfidence,
        string? Comment);

    private sealed record GoldsetAnswerItem(
        string Id,
        string Locale,
        string OriginalMessage,
        string Question,
        string Answer,
        bool ExpectNeedsClarification,
        string? ExpectIntent,
        string? Comment);

    private sealed record MessageOutcome(GoldsetItem Item, InboundAnalysis Analysis, bool Hit, bool NeedsMatch, bool IntentMatch);

    private sealed record AnswerOutcome(GoldsetAnswerItem Item, InboundAnalysis Analysis, bool Hit);

    private sealed record RejectedQuestion(string Id, string Locale, string Message, string Question, string Violation);

    private sealed class GoldsetReport(string? modelId)
    {
        public List<MessageOutcome> Messages { get; } = [];

        public List<AnswerOutcome> Answers { get; } = [];

        public List<RejectedQuestion> Rejected { get; } = [];

        public List<string> Leaks { get; } = [];

        public List<string> ComposeFailures { get; } = [];

        public List<string> CheckedAnalysisInjectionItems { get; } = [];

        public List<string> UncheckedAnalysisInjectionItems { get; } = [];

        public List<string> ForbiddenIntentViolations { get; } = [];

        public List<string> ConfidenceViolations { get; } = [];

        public HashSet<string> ExercisedInjectionItems { get; } = [];

        public List<string> UnexercisedInjectionItems { get; } = [];

        public List<string> LanguageMismatches { get; } = [];

        public List<string> NonIsoQuestions { get; } = [];

        public List<string> AnswerInvariantViolations { get; } = [];

        public int ComposedQuestions { get; set; }

        public int GuardPasses { get; set; }

        public int LanguageChecked { get; set; }

        public int LanguageMatches { get; set; }

        public double HitRate => Messages.Count == 0 ? 0 : (double)Messages.Count(m => m.Hit) / Messages.Count;

        public int ComposeAttempts => ComposedQuestions + ComposeFailures.Count;

        public double GuardPassRate => ComposeAttempts == 0 ? 0 : (double)GuardPasses / ComposeAttempts;

        public double AnswerHitRate => Answers.Count == 0 ? 0 : (double)Answers.Count(a => a.Hit) / Answers.Count;

        public void Print()
        {
            var text = new StringBuilder();
            text.AppendLine(ReportSeparator);
            text.AppendLine($"GOLDSET RESULT model={modelId ?? "(configured default)"}");
            text.AppendLine($"needsClarification/intent hit rate {HitRate:P1} ({Messages.Count(m => m.Hit)}/{Messages.Count})");
            text.AppendLine(
                $"  needsClarification only: {Messages.Count(m => m.NeedsMatch)}/{Messages.Count}, " +
                $"intent checks: {Messages.Count(m => m.Item.ExpectIntent != null && m.IntentMatch)}/{Messages.Count(m => m.Item.ExpectIntent != null)}");
            foreach (var locale in Messages.Select(m => m.Item.Locale).Distinct())
            {
                var localeItems = Messages.Where(m => m.Item.Locale == locale).ToList();
                text.AppendLine($"  {locale}: {localeItems.Count(m => m.Hit)}/{localeItems.Count}");
            }

            text.AppendLine($"guard pass rate {GuardPassRate:P1} ({GuardPasses}/{ComposeAttempts} compose attempts, {ComposedQuestions} composed), compose failures: {ComposeFailures.Count}");
            text.AppendLine(
                $"injection items exercised (raw question produced): {ExercisedInjectionItems.Count}/{ExercisedInjectionItems.Count + UnexercisedInjectionItems.Count}, " +
                $"unexercised: {UnexercisedInjectionItems.Count}");
            text.AppendLine(
                $"analysis injection items checked (forbiddenIntent/maxConfidence): {CheckedAnalysisInjectionItems.Count}/" +
                $"{CheckedAnalysisInjectionItems.Count + UncheckedAnalysisInjectionItems.Count}, unchecked: {UncheckedAnalysisInjectionItems.Count}, " +
                $"forbiddenIntent violations: {ForbiddenIntentViolations.Count}, maxConfidence violations: {ConfidenceViolations.Count}");
            text.AppendLine($"answer analysis hit rate {AnswerHitRate:P1} ({Answers.Count(a => a.Hit)}/{Answers.Count}), invariant violations: {AnswerInvariantViolations.Count}");
            text.AppendLine($"question language markers: {LanguageMatches}/{LanguageChecked} matched (heuristic, informational)");
            text.AppendLine($"questions with non-ISO date/time wording: {NonIsoQuestions.Count}");

            AppendSection(text, "OUTLIERS (message items)", Messages.Where(m => !m.Hit).Select(m =>
                $"{m.Item.Id} [{m.Item.Locale}] \"{m.Item.Message}\" expectedNC={m.Item.ExpectNeedsClarification} actualNC={m.Analysis.NeedsClarification} " +
                $"expectedIntent={m.Item.ExpectIntent ?? "-"} actualIntent={m.Analysis.Intent} confidence={m.Analysis.Confidence}" +
                (m.Analysis.FailureReason != null ? $" failure={m.Analysis.FailureReason}" : string.Empty)));
            AppendSection(text, "OUTLIERS (answer items)", Answers.Where(a => !a.Hit).Select(a =>
                $"{a.Item.Id} [{a.Item.Locale}] expectedNC={a.Item.ExpectNeedsClarification} actualNC={a.Analysis.NeedsClarification} " +
                $"expectedIntent={a.Item.ExpectIntent ?? "-"} actualIntent={a.Analysis.Intent} confidence={a.Analysis.Confidence}"));
            AppendSection(text, "REJECTED QUESTIONS", Rejected.Select(r =>
                $"{r.Id} [{r.Locale}] message=\"{r.Message}\" question=\"{r.Question}\" violation={r.Violation}"));
            AppendSection(text, "UNEXERCISED INJECTION ITEMS", UnexercisedInjectionItems);
            AppendSection(text, "INJECTION LEAKS", Leaks);
            AppendSection(text, "FORBIDDEN INTENT VIOLATIONS", ForbiddenIntentViolations);
            AppendSection(text, "MAX CONFIDENCE VIOLATIONS", ConfidenceViolations);
            AppendSection(text, "UNCHECKED ANALYSIS INJECTION ITEMS", UncheckedAnalysisInjectionItems);
            AppendSection(text, "ANSWER INVARIANT VIOLATIONS", AnswerInvariantViolations);
            AppendSection(text, "QUESTION LANGUAGE MISMATCHES", LanguageMismatches);
            AppendSection(text, "COMPOSE FAILURES", ComposeFailures);
            AppendSection(text, "NON-ISO DATE/TIME QUESTIONS", NonIsoQuestions);
            text.AppendLine(ReportSeparator);
            TestContext.Out.WriteLine(text.ToString());
        }

        private static void AppendSection(StringBuilder text, string title, IEnumerable<string> lines)
        {
            var list = lines.ToList();
            text.AppendLine($"{title}: {list.Count}");
            foreach (var line in list)
            {
                text.AppendLine($"  {line}");
            }
        }
    }

    private sealed class ModelPinningCompletionService(IOneShotCompletionService inner, string? modelId) : IOneShotCompletionService
    {
        public string? LastContent { get; private set; }

        public string? LastError { get; private set; }

        public void Reset()
        {
            LastContent = null;
            LastError = null;
        }

        public async Task<OneShotCompletionResult> CompleteAsync(
            string systemPrompt, string userMessage, string? model = null, CancellationToken cancellationToken = default)
        {
            var result = await inner.CompleteAsync(systemPrompt, userMessage, model ?? modelId, cancellationToken);
            LastContent = result.Success ? result.Content : null;
            LastError = result.Error;
            return result;
        }
    }

    private sealed class FixedCompanyClock(DateTime nowUtc) : ICompanyClock
    {
        public Task<DateTime> GetTodayAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(DateTime.SpecifyKind(nowUtc.Date, DateTimeKind.Utc));

        public Task<DateOnly> GetTodayDateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(DateOnly.FromDateTime(nowUtc));

        public Task<DateTimeOffset> GetNowAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DateTimeOffset(nowUtc, TimeSpan.Zero));

        public Task<TimeZoneInfo> GetTimeZoneAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(TimeZoneInfo.Utc);

        public Task<CompanyTimeZoneResolution> GetTimeZoneResolutionAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class SingleShiftReader(IReadOnlyList<ClarificationShift> shifts) : IInboundShiftContextReader
    {
        public Task<IReadOnlyList<ClarificationShift>> GetShiftsAsync(
            Guid clientId, DateOnly fromDate, DateOnly untilDate, int maxCount, CancellationToken cancellationToken = default) =>
            Task.FromResult(shifts);
    }
}
