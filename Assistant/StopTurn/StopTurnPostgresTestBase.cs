// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Base of the stop-turn integration tests against the shared integration database. It boots one hardened
/// host per fixture (real container, scripted provider and skill bridge), picks an existing user, inserts its
/// own model row and gives every test its own conversation. Everything a test writes is keyed by the
/// INTEGRATION_TEST_ prefix - conversation id, model id, skill name, first characters of the user message -
/// and the cleanup deletes only rows carrying that prefix, twice, the second time after the fire-and-forget
/// post-turn tasks had time to write. It never deletes by a business-plausible value. The requests are worded
/// so that no recipe trigger matches: a matching recipe would open a run row for the real user of the shared
/// database. Known side effect that cannot be avoided while the real chat service runs: it bumps the access
/// counters (access_count, last_accessed_at) of the shared knowledge memories it retrieves.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.IntegrationTest.Assistant.StopTurn;

[Category("RealDatabase")]
public abstract class StopTurnPostgresTestBase
{
    protected const string Prefix = "INTEGRATION_TEST_";
    protected const string WriteSkill = Prefix + "write_skill";
    protected const string SecondWriteSkill = Prefix + "second_write_skill";
    protected const string ReadSkill = "get_integration_test_lookup";
    protected const string WriteLabel = "Write something";
    protected const string Language = "en";
    protected static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private const int ModelSuffixLength = 8;
    private const int BackgroundGraceMs = 1500;
    private const int PollIntervalMs = 100;

    protected StopTurnTestWebApplicationFactory Factory { get; private set; } = null!;

    protected string UserId { get; private set; } = null!;

    protected string ModelKey { get; private set; } = null!;

    protected string ConversationKey { get; set; } = null!;

    protected ScriptedLlmProvider Provider => Factory.Provider;

    protected ScriptedSkillBridge Skills => Factory.Skills;

    [OneTimeSetUp]
    public async Task StopTurnOneTimeSetUp()
    {
        Factory = new StopTurnTestWebApplicationFactory();
        _ = Factory.Services;

        UserId = (string)(await ScalarAsync("SELECT id FROM \"AspNetUsers\" ORDER BY id LIMIT 1"))!;
        ModelKey = Prefix + "M_" + Guid.NewGuid().ToString("N")[..ModelSuffixLength];

        await CleanupAsync();
        await using var context = NewContext();
        context.Set<LLMModel>().Add(new LLMModel
        {
            ModelId = ModelKey,
            ModelName = ModelKey,
            ApiModelId = ModelKey,
            ProviderId = ScriptedLlmProvider.ProviderKey,
            IsEnabled = true,
            MaxTokens = 4096,
            ContextWindow = 128_000
        });
        await context.SaveChangesAsync();
    }

    [OneTimeTearDown]
    public async Task StopTurnOneTimeTearDown()
    {
        await Task.Delay(BackgroundGraceMs);
        await CleanupAsync();
        Factory.Dispose();
    }

    [SetUp]
    public void StopTurnSetUp()
    {
        Provider.Reset();
        Skills.Reset();
        ConversationKey = Prefix + "conv_" + Guid.NewGuid().ToString("N");
    }

    [TearDown]
    public async Task StopTurnTearDown()
    {
        await CleanupAsync(keepModel: true);
        await Task.Delay(BackgroundGraceMs);
        await CleanupAsync(keepModel: true);
    }

    protected static DataBaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseNpgsql(TestHostDatabase.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    protected LLMContext NewContext(string message, Guid? turnId = null, CancellationToken stopToken = default) => new()
    {
        Message = Prefix + message,
        UserId = UserId,
        ConversationId = ConversationKey,
        ModelId = ModelKey,
        Language = Language,
        TurnId = turnId ?? Guid.NewGuid(),
        StopToken = stopToken,
        AvailableFunctions = new List<LLMFunction>
        {
            new() { Name = WriteSkill, Labels = new Dictionary<string, string> { [Language] = WriteLabel } },
            new() { Name = SecondWriteSkill },
            new() { Name = ReadSkill }
        }
    };

    protected async Task<List<SseChunk>> RunTurnAsync(IServiceScope scope, LLMContext context)
    {
        var chunks = new List<SseChunk>();
        await foreach (var chunk in scope.ServiceProvider.GetRequiredService<ILLMService>().ProcessStreamAsync(context))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    protected static async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(TestHostDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    protected static async Task<int> ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(TestHostDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync();
    }

    protected static async Task<List<object?[]>> RowsAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(TestHostDatabase.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var rows = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var index = 0; index < row.Length; index++)
            {
                row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            }

            rows.Add(row);
        }

        return rows;
    }

    protected async Task<List<(string Role, string Content, DateTime CreateTime)>> MessagesAsync()
    {
        var rows = await RowsAsync(
            "SELECT m.role, m.content, m.create_time FROM llm_messages m "
            + "JOIN llm_conversations c ON c.id = m.conversation_id "
            + "WHERE c.conversation_id = @conversation ORDER BY m.create_time",
            ("conversation", ConversationKey));
        return rows.Select(row => ((string)row[0]!, (string)row[1]!, (DateTime)row[2]!)).ToList();
    }

    protected async Task<List<object?[]>> UsagesAsync() => await RowsAsync(
        "SELECT id, has_error, error_message, functions_called, tool_iterations FROM llm_usages "
        + "WHERE conversation_id = @conversation ORDER BY create_time",
        ("conversation", ConversationKey));

    protected async Task<object?[]?> AnchorAsync() => (await RowsAsync(
        "SELECT user_message, assistant_answer_excerpt, superseded_at_utc, calls_json, create_time_utc FROM assistant_last_actions "
        + "WHERE conversation_id = @conversation",
        ("conversation", ConversationKey))).FirstOrDefault();

    protected async Task<List<object?[]>> TrajectoriesAsync(Guid turnId) => await RowsAsync(
        "SELECT was_interrupted, interrupted_phase, llm_chosen_skill, was_executed, was_successful, correction_type "
        + "FROM skill_selection_trajectories WHERE turn_id = @turn",
        ("turn", turnId));

    protected static async Task<List<object?[]>> WaitForRowsAsync(Func<Task<List<object?[]>>> read, int expected)
    {
        var deadline = DateTime.UtcNow + Patience;
        List<object?[]> rows;
        do
        {
            rows = await read();
            if (rows.Count >= expected)
            {
                break;
            }

            await Task.Delay(PollIntervalMs);
        }
        while (DateTime.UtcNow < deadline);

        return rows;
    }

    protected static string Json(object value) => JsonSerializer.Serialize(value);

    private static async Task CleanupAsync(bool keepModel = false)
    {
        var likePrefix = ("prefix", (object)Prefix);
        string[] statements =
        [
            "DELETE FROM assistant_last_actions WHERE starts_with(conversation_id, @prefix)",
            "DELETE FROM recipe_runs WHERE starts_with(conversation_id, @prefix)",
            "DELETE FROM pending_recipes WHERE starts_with(conversation_id, @prefix)",
            "DELETE FROM llm_messages WHERE conversation_id IN (SELECT id FROM llm_conversations WHERE starts_with(conversation_id, @prefix))",
            "DELETE FROM llm_usages WHERE starts_with(conversation_id, @prefix)",
            "DELETE FROM llm_conversations WHERE starts_with(conversation_id, @prefix)",
            "DELETE FROM skill_selection_trajectories WHERE starts_with(intent_excerpt, @prefix)",
            "DELETE FROM skill_usage_records WHERE starts_with(skill_name, @prefix)"
        ];

        foreach (var statement in statements)
        {
            await ExecuteAsync(statement, likePrefix);
        }

        if (!keepModel)
        {
            await ExecuteAsync("DELETE FROM llm_models WHERE starts_with(model_id, @prefix)", likePrefix);
        }
    }
}
