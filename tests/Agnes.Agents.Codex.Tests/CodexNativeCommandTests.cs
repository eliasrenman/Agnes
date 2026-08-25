using System.Text.Json;
using Agnes.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Agents.Codex.Tests;

public sealed class CodexNativeCommandTests
{
    [Fact]
    public async Task Session_discovers_goal_and_provider_named_mode_commands_and_executes_typed_rpcs()
    {
        var (client, server) = FakeCodexAppServer.Create();
        await using var _ = server;
        await using var connection = new CodexConnection(client, client, NullLogger.Instance);
        server.Goal = Goal("Existing goal", "active");
        server.CollaborationModes.Add(new
        {
            name = "Brain Storm",
            mode = "creative",
            model = "gpt-creative",
            reasoning_effort = "high",
        });

        await connection.InitializeAsync(default);
        Assert.True(server.InitializedNotificationReceived);
        var inner = await connection.StartThreadAsync(
            "/tmp", "on-request", "workspace-write", "gpt-test", default);
        await using var session = new CodexAppServerAdapter.ConnectionOwningSession(inner, new NoopOwner());

        Assert.Equal(["goal", "goal-clear", "brain-storm"], session.Commands.Select(c => c.Name));
        Assert.Equal("Existing goal", session.ProviderGoal?.Objective);
        Assert.Equal("Brain Storm", Assert.Single(session.Modes).Name);

        await session.ExecuteCommandAsync(CodexAgentSession.GoalCommandId, "Ship native commands");
        Assert.Equal("Ship native commands", server.LastGoalObjective);
        var changed = await session.Events.ReadAsync();
        Assert.Equal("Ship native commands", Assert.IsType<ProviderGoalChangedEvent>(changed).Goal?.Objective);

        var modeCommand = Assert.Single(session.Commands, c => c.Name == "brain-storm");
        await session.ExecuteCommandAsync(modeCommand.Id, null);
        Assert.Equal("creative", Assert.IsType<ModeChangedEvent>(await session.Events.ReadAsync()).ModeId);

        server.OnTurn = rpc => rpc.NotifyWithParameterObjectAsync("turn/completed", new
        {
            threadId = "th-1",
            turn = new { id = "tn-1", status = "completed" },
        });
        await session.PromptAsync([new TextContent("hello")]);
        Assert.Equal("creative", server.LastTurnCollaborationMode);
        Assert.IsType<TurnEndedEvent>(await session.Events.ReadAsync());

        await session.ExecuteCommandAsync(CodexAgentSession.ClearGoalCommandId, null);
        Assert.Equal(1, server.GoalClearCount);
        Assert.Null(Assert.IsType<ProviderGoalChangedEvent>(await session.Events.ReadAsync()).Goal);
    }

    [Fact]
    public async Task Provider_goal_notification_updates_event_sourced_state()
    {
        var (client, server) = FakeCodexAppServer.Create();
        await using var _ = server;
        await using var connection = new CodexConnection(client, client, NullLogger.Instance);
        await connection.InitializeAsync(default);
        var session = await connection.StartThreadAsync(
            "/tmp", "on-request", "workspace-write", "gpt-test", default);

        await server.NotifyAsync("thread/goal/updated", new
        {
            threadId = "th-1",
            turnId = (string?)null,
            goal = JsonSerializer.Deserialize<object>(Goal("Live goal", "paused").GetRawText()),
        });

        var changed = Assert.IsType<ProviderGoalChangedEvent>(await session.Events.ReadAsync());
        Assert.Equal("Live goal", changed.Goal?.Objective);
        Assert.Equal("paused", session.ProviderGoal?.Status);
    }

    private static JsonElement Goal(string objective, string status)
        => JsonSerializer.SerializeToElement(new
        {
            threadId = "th-1",
            objective,
            status,
            tokenBudget = (long?)null,
            tokensUsed = 4L,
            timeUsedSeconds = 8L,
        });

    private sealed class NoopOwner : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
