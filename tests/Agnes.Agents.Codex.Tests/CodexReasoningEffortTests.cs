using System.Text.Json;
using Agnes.Abstractions;
using Agnes.Agents.Codex.Wire;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Agents.Codex.Tests;

public sealed class CodexReasoningEffortTests
{
    [Fact]
    public async Task Model_list_reads_every_page_and_maps_supported_and_default_efforts()
    {
        var (client, server) = FakeCodexAppServer.Create();
        await using var _ = server;
        await using var connection = new CodexConnection(client, client, NullLogger.Instance);
        server.ModelPages.Add(Page("1", Model("gpt-a", "GPT A", "medium", "low", "medium")));
        server.ModelPages.Add(Page(null, Model("gpt-b", "GPT B", "high", "high")));

        await connection.InitializeAsync(default);
        var models = await connection.ListModelsAsync(default);

        Assert.Equal([null, "1"], server.ModelListCursors);
        Assert.Equal(["gpt-a", "gpt-b"], models.Select(m => m.Id));
        var mapped = CodexAppServerAdapter.ToModelInfo(models[0]);
        Assert.Equal("medium", mapped.DefaultReasoningEffortId);
        Assert.Equal(["low", "medium"], mapped.SupportedReasoningEfforts!.Select(e => e.Id));
        Assert.Equal("Low", mapped.SupportedReasoningEfforts![0].DisplayName);
    }

    [Fact]
    public void Capability_uses_provider_default_and_rejects_unknown_requested_value()
    {
        var model = Model("gpt-a", "GPT A", "medium", "low", "medium");

        var capability = CodexAppServerAdapter.CreateReasoningCapability(model, requestedEffort: null);

        Assert.Equal("medium", capability!.CurrentEffortId);
        var error = Assert.Throws<ArgumentException>(
            () => CodexAppServerAdapter.CreateReasoningCapability(model, "extreme"));
        Assert.Contains("low, medium", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adapter_live_discovery_maps_metadata_and_failure_falls_back_without_guessed_efforts()
    {
        var spec = new CodexLaunchSpec();
        var live = new CodexAppServerAdapter(
            spec, NullLoggerFactory.Instance,
            _ => Task.FromResult<IReadOnlyList<CodexModel>>([Model("gpt-a", "GPT A", "medium", "low", "medium")]));
        var discovered = Assert.Single((await live.ListModelsAsync())!);
        Assert.Equal("medium", discovered.DefaultReasoningEffortId);

        var failed = new CodexAppServerAdapter(
            spec, NullLoggerFactory.Instance,
            _ => Task.FromException<IReadOnlyList<CodexModel>>(new InvalidOperationException("probe failed")));
        var fallback = await ModelCatalog.ResolveAsync(failed);

        Assert.Equal(failed.StaticModels, fallback);
        Assert.All(fallback, model =>
        {
            Assert.Null(model.SupportedReasoningEfforts);
            Assert.Null(model.DefaultReasoningEffortId);
        });
    }

    [Fact]
    public async Task Runtime_change_is_validated_and_the_confirmed_effort_is_sent_on_the_next_turn()
    {
        var (client, server) = FakeCodexAppServer.Create();
        await using var _ = server;
        await using var connection = new CodexConnection(client, client, NullLogger.Instance);
        await connection.InitializeAsync(default);
        var values = new[]
        {
            new ReasoningEffortInfo("low", "Low"),
            new ReasoningEffortInfo("high", "High"),
        };
        var session = await connection.StartThreadAsync(
            "/tmp", "on-request", "workspace-write", "gpt-a", default,
            new ReasoningEffortCapability(values, "low", "low", SupportsRuntimeChanges: true));

        await session.SetReasoningEffortAsync("high");
        server.OnTurn = rpc => rpc.NotifyWithParameterObjectAsync("turn/completed", new
        {
            threadId = "th-1",
            turn = new { id = "tn-1", status = "completed" },
        });

        await session.PromptAsync([new TextContent("hello")]);

        Assert.Equal("high", server.LastTurnEffort);
        await Assert.ThrowsAsync<ArgumentException>(() => session.SetReasoningEffortAsync("extreme"));
    }

    private static CodexModel Model(
        string id, string displayName, string defaultEffort, params string[] efforts)
        => new()
        {
            Id = id,
            Model = id,
            DisplayName = displayName,
            IsDefault = id == "gpt-a",
            SupportedReasoningEfforts = efforts
                .Select(e => new CodexReasoningEffortOption(e, $"Use {e} reasoning"))
                .ToArray(),
            DefaultReasoningEffort = defaultEffort,
        };

    private static JsonElement Page(string? nextCursor, params CodexModel[] models)
        => JsonSerializer.SerializeToElement(new { data = models, nextCursor }, CodexJson.CreateOptions());
}
