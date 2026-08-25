using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Tests;

public sealed class ReasoningEffortViewModelTests
{
    private sealed class Host : StubAgnesHost
    {
        public string? Requested { get; private set; }
        public override Task SetReasoningEffortAsync(string sessionId, string effortId)
        {
            Requested = effortId;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Selection_waits_for_confirmation_and_is_disabled_during_a_turn()
    {
        var host = new Host();
        var values = new[] { new ReasoningEffortInfo("low", "Low"), new ReasoningEffortInfo("high", "High") };
        var capability = new ReasoningEffortCapability(values, "low", "low", SupportsRuntimeChanges: true);
        var view = new SessionView("s1");
        view.ApplySnapshot(new SessionSnapshot(
            new SessionInfo("s1", "codex", "/work", 0, ReasoningEffortId: "low", ReasoningEffort: capability), [], 0));
        var vm = new SessionViewModel(host, view, ImmediateDispatcher.Instance, "Codex");

        vm.SelectedReasoningEffort = values[1];
        await WaitUntilAsync(() => host.Requested == "high");
        Assert.Equal("low", vm.CurrentReasoningEffortId); // host call alone is not confirmation

        view.Apply(new ReasoningEffortChangedEvent(capability with { CurrentEffortId = "high" }) { Sequence = 1 });
        Assert.Equal("high", vm.CurrentReasoningEffortId);

        view.Apply(new ThoughtChunkEvent(new TextContent("working")) { Sequence = 2 });
        Assert.False(vm.CanChangeReasoningEffort);
        view.Apply(new TurnEndedEvent(StopReason.EndTurn) { Sequence = 3 });
        Assert.True(vm.CanChangeReasoningEffort);
    }

    [Fact]
    public void Unsupported_sessions_hide_the_selector()
    {
        var view = new SessionView("s1");
        view.ApplySnapshot(new SessionSnapshot(new SessionInfo("s1", "opencode", "/work", 0), [], 0));
        var vm = new SessionViewModel(new Host(), view, ImmediateDispatcher.Instance, "OpenCode");

        Assert.False(vm.HasReasoningEffortSelector);
        Assert.Empty(vm.AvailableReasoningEfforts);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
