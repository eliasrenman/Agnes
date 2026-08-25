using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Tests;

public sealed class ProviderCommandViewModelTests
{
    private sealed class Host : StubAgnesHost
    {
        public (string Id, string? Argument)? Executed { get; private set; }
        public bool Fail { get; set; }

        public override Task ExecuteAgentCommandAsync(string sessionId, string commandId, string? argument)
        {
            if (Fail)
            {
                throw new InvalidOperationException("provider rejected command");
            }

            Executed = (commandId, argument);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Live_session_commands_are_suggested_and_submitted_by_opaque_id()
    {
        var host = new Host();
        var commands = new[]
        {
            new AgentCommandInfo("opaque-goal-id", "goal", "Set goal", "objective", AcceptsArguments: true),
        };
        var view = View(commands);
        var vm = new SessionViewModel(host, view, ImmediateDispatcher.Instance, "Codex");

        vm.PromptText = "/go";
        var suggestion = Assert.Single(vm.SlashSuggestions, s => s.AgentCommandId == "opaque-goal-id");
        vm.ApplySlashCommand.Execute(suggestion);
        Assert.Equal("/goal ", vm.PromptText);
        Assert.Equal("/goal <objective>", Assert.Single(vm.SlashSuggestions).DisplayToken);

        vm.PromptText += "Finish the feature";
        Assert.Equal("/goal <objective>", Assert.Single(vm.SlashSuggestions).DisplayToken);
        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(("opaque-goal-id", "Finish the feature"), host.Executed);
        Assert.Equal(string.Empty, vm.PromptText);
    }

    [Fact]
    public async Task Failed_provider_command_keeps_composer_text_and_goal_events_update_state()
    {
        var host = new Host { Fail = true };
        var view = View([new AgentCommandInfo("goal", "goal", "Set goal", AcceptsArguments: true)]);
        var vm = new SessionViewModel(host, view, ImmediateDispatcher.Instance, "Codex")
        {
            PromptText = "/goal Keep this",
        };

        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal("/goal Keep this", vm.PromptText);

        view.Apply(new ProviderGoalChangedEvent(new ProviderGoalInfo("Live objective", "active")) { Sequence = 1 });
        Assert.True(vm.HasProviderGoal);
        Assert.Contains("Live objective", vm.ProviderGoalSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void Replayed_live_command_discovery_restores_autocomplete_when_snapshot_metadata_is_dormant()
    {
        var view = View([]);
        view.Apply(new AgentCommandsChangedEvent(
            [new AgentCommandInfo("mode-plan", "plan", "Switch to Plan mode")]) { Sequence = 1 });

        var vm = new SessionViewModel(new Host(), view, ImmediateDispatcher.Instance, "Codex")
        {
            PromptText = "/",
        };

        var plan = Assert.Single(vm.SlashSuggestions, s => s.AgentCommandId == "mode-plan");
        Assert.Equal("/plan", plan.DisplayToken);
    }

    private static SessionView View(IReadOnlyList<AgentCommandInfo> commands)
    {
        var view = new SessionView("s1");
        view.ApplySnapshot(new SessionSnapshot(
            new SessionInfo("s1", "codex", "/work", 0, Commands: commands), [], 0));
        return view;
    }
}
