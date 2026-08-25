using System.Threading.Channels;
using Agnes.Abstractions;
using Agnes.Abstractions.Events;
using Agnes.Host.Events;
using Agnes.Host.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Host.Tests;

public sealed class ReasoningEffortHostTests
{
    private sealed class NullBroadcaster : ISessionBroadcaster
    {
        public Task PublishAsync(string sessionId, SessionEvent @event) => Task.CompletedTask;
    }

    private sealed class VetoReasoning : IEventInterceptor<BeforeReasoningEffortChangeEvent>
    {
        public ValueTask InterceptAsync(BeforeReasoningEffortChangeEvent evt, CancellationToken ct = default)
        {
            evt.Cancel("policy");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CapableAdapter : IAgentAdapter, IModelListingAdapter
    {
        public AgentDescriptor Descriptor { get; } = new() { Id = "capable", DisplayName = "Capable" };
        public AgentSessionOptions? LastOptions { get; private set; }
        public CapableSession? LastSession { get; private set; }
        public IReadOnlyList<ModelInfo> StaticModels { get; } =
        [
            new("a", "A", SupportedReasoningEfforts:
                [new("low", "Low"), new("high", "High")], DefaultReasoningEffortId: "high"),
            new("b", "B", SupportedReasoningEfforts:
                [new("medium", "Medium")], DefaultReasoningEffortId: "medium"),
            new("c", "C", SupportedReasoningEfforts:
                [new("low", "Low"), new("medium", "Medium")], DefaultReasoningEffortId: "medium"),
        ];

        public Task<IReadOnlyList<ModelInfo>?> ListModelsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ModelInfo>?>(StaticModels);

        public Task<IAgentSession> StartSessionAsync(AgentSessionOptions options, CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            var model = StaticModels.First(m => m.Id == (options.ModelId ?? "a"));
            var supported = model.SupportedReasoningEfforts!;
            var effort = options.ReasoningEffortId ?? model.DefaultReasoningEffortId;
            if (supported.All(e => e.Id != effort))
            {
                throw new ArgumentException($"Unsupported effort '{effort}'. Valid values: {string.Join(", ", supported.Select(e => e.Id))}.");
            }

            LastSession = new CapableSession(new ReasoningEffortCapability(
                supported, model.DefaultReasoningEffortId, effort, SupportsRuntimeChanges: true));
            return Task.FromResult<IAgentSession>(LastSession);
        }
    }

    private sealed class CapableSession(ReasoningEffortCapability capability) : IAgentSession
    {
        private readonly Channel<SessionEvent> _events = Channel.CreateUnbounded<SessionEvent>();
        public string AgentSessionId { get; } = Guid.NewGuid().ToString();
        public ChannelReader<SessionEvent> Events => _events.Reader;
        public ReasoningEffortCapability? ReasoningEffort { get; private set; } = capability;
        public IReadOnlyList<AgentCommandInfo> Commands { get; } =
            [new("provider.live.command", "live", "A live command", AcceptsArguments: true)];
        public (string Id, string? Argument)? ExecutedCommand { get; private set; }
        public Task<StopReason> PromptAsync(IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken = default)
            => Task.FromResult(StopReason.EndTurn);
        public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RespondToPermissionAsync(string requestId, string optionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetReasoningEffortAsync(string effortId, CancellationToken cancellationToken = default)
        {
            if (ReasoningEffort!.SupportedValues.All(e => e.Id != effortId))
            {
                throw new ArgumentException("unsupported", nameof(effortId));
            }

            ReasoningEffort = ReasoningEffort with { CurrentEffortId = effortId };
            return Task.CompletedTask;
        }
        public Task ExecuteCommandAsync(string commandId, string? argument, CancellationToken cancellationToken = default)
        {
            ExecutedCommand = (commandId, argument);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            _events.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private static SessionManager Manager(IAgentAdapter adapter, IEventStore store, EventBus? bus = null)
        => new(TestPluginRegistries.Agents(adapter), store, new NullBroadcaster(), NullLoggerFactory.Instance, eventBus: bus);

    [Fact]
    public async Task Open_runtime_change_and_snapshot_use_confirmed_effective_state_and_persist_it()
    {
        var store = new InMemoryEventStore();
        var adapter = new CapableAdapter();
        await using var manager = Manager(adapter, store);

        var info = await manager.OpenSessionAsync(
            "capable", "/tmp/work", useSandbox: false, modelId: "a", reasoningEffortId: "low");
        Assert.Equal("low", adapter.LastOptions!.ReasoningEffortId);
        Assert.Equal("low", info.ReasoningEffortId);

        await manager.SetReasoningEffortAsync(info.SessionId, "high");

        Assert.Equal("high", (await store.ListSessionsAsync()).Single().ReasoningEffortId);
        var snapshot = await manager.GetSnapshotAsync(info.SessionId, 0);
        Assert.Equal("high", snapshot.Session.ReasoningEffortId);
        Assert.Contains(snapshot.Events, e => e is ReasoningEffortChangedEvent changed
            && changed.Capability.CurrentEffortId == "high");
    }

    [Fact]
    public async Task Spine_veto_keeps_the_previous_effort()
    {
        var bus = new EventBus();
        bus.Intercept(new VetoReasoning());
        var store = new InMemoryEventStore();
        await using var manager = Manager(new CapableAdapter(), store, bus);
        var info = await manager.OpenSessionAsync(
            "capable", "/tmp/work", useSandbox: false, modelId: "a", reasoningEffortId: "low");

        await manager.SetReasoningEffortAsync(info.SessionId, "high");

        Assert.Equal("low", (await store.ListSessionsAsync()).Single().ReasoningEffortId);
    }

    [Fact]
    public async Task Model_switch_uses_the_new_default_and_restart_and_fork_reuse_the_effective_value()
    {
        var store = new InMemoryEventStore();
        var adapter = new CapableAdapter();
        await using var manager = Manager(adapter, store);
        var source = Path.Combine(Path.GetTempPath(), $"agnes-effort-source-{Guid.NewGuid():n}");
        Directory.CreateDirectory(source);
        var info = await manager.OpenSessionAsync(
            "capable", source, useSandbox: false, modelId: "a", reasoningEffortId: "low");

        await manager.SwitchModelAsync(info.SessionId, "c");
        Assert.Equal("low", adapter.LastOptions!.ReasoningEffortId); // retained by a compatible model

        await manager.SwitchModelAsync(info.SessionId, "b");
        Assert.Equal("medium", adapter.LastOptions!.ReasoningEffortId);
        Assert.Equal("medium", (await store.ListSessionsAsync()).Single().ReasoningEffortId);

        await manager.RestartAgentAsync(info.SessionId);
        Assert.Equal("medium", adapter.LastOptions!.ReasoningEffortId);

        var target = Path.Combine(Path.GetTempPath(), $"agnes-effort-fork-{Guid.NewGuid():n}");
        try
        {
            var fork = await manager.ForkSessionAsync(info.SessionId, target, copySandbox: false);
            Assert.Equal("medium", fork.ReasoningEffortId);
            Assert.Equal("medium", adapter.LastOptions!.ReasoningEffortId);
        }
        finally
        {
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            if (Directory.Exists(source)) Directory.Delete(source, recursive: true);
        }
    }

    [Fact]
    public async Task Unsupported_adapter_rejects_a_reasoning_effort()
    {
        await using var manager = Manager(new ScriptedAgentAdapter(), new InMemoryEventStore());
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => manager.OpenSessionAsync(
            "scripted", "/tmp/work", useSandbox: false, reasoningEffortId: "high"));
        Assert.Contains("does not support", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stale_profile_value_is_forwarded_and_rejected_with_current_supported_values()
    {
        var profile = new Agnes.Protocol.LaunchProfile(
            "p1", "Stale", "capable", "/tmp/work", UseSandbox: false, ModelId: "a",
            ReasoningEffortId: "medium");
        var request = profile.ToOpenSessionRequest();
        await using var manager = Manager(new CapableAdapter(), new InMemoryEventStore());

        var error = await Assert.ThrowsAsync<ArgumentException>(() => manager.OpenSessionAsync(
            request.AdapterId, request.WorkingDirectory, useSandbox: request.UseSandbox,
            modelId: request.ModelId, reasoningEffortId: request.ReasoningEffortId));

        Assert.Contains("low, high", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_executes_only_commands_advertised_by_the_live_session()
    {
        var adapter = new CapableAdapter();
        await using var manager = Manager(adapter, new InMemoryEventStore());
        var info = await manager.OpenSessionAsync("capable", "/tmp/work", useSandbox: false, modelId: "a");

        Assert.Equal("provider.live.command", Assert.Single(info.Commands!).Id);
        await manager.ExecuteAgentCommandAsync(info.SessionId, "provider.live.command", "value");
        Assert.Equal(("provider.live.command", "value"), adapter.LastSession!.ExecutedCommand);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => manager.ExecuteAgentCommandAsync(info.SessionId, "provider.unknown", null));
    }
}
