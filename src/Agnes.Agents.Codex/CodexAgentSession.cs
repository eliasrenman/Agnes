using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Agnes.Abstractions;
using Agnes.Agents.Codex.Wire;
using Microsoft.Extensions.Logging;

namespace Agnes.Agents.Codex;

/// <summary>Outbound calls a session makes to the Codex app-server (via the connection).</summary>
internal interface ICodexRpc
{
    Task<string> StartTurnAsync(
        string threadId, IReadOnlyList<CodexUserInput> input, string? effort,
        CodexCollaborationMode? collaborationMode, CancellationToken cancellationToken);
    Task<CodexGoal> SetGoalAsync(string threadId, string objective, CancellationToken cancellationToken);
    Task<bool> ClearGoalAsync(string threadId, CancellationToken cancellationToken);
    Task InterruptAsync(string threadId);
}

internal sealed record CodexDiscoveredCapabilities(
    bool GoalsSupported,
    CodexGoal? Goal,
    IReadOnlyList<CodexCollaborationModeMask> CollaborationModes);

/// <summary>
/// An <see cref="IAgentSession"/> backed by one Codex thread on a connected <c>codex app-server</c>.
/// A prompt drives one <c>turn/start</c> and completes when the matching <c>turn/completed</c>
/// notification arrives (prompts are serial per session, so a single active-turn slot suffices).
/// </summary>
internal sealed class CodexAgentSession : IAgentSession
{
    private readonly ICodexRpc _rpc;
    private readonly ILogger _logger;
    private readonly CodexMap _map = new();
    private readonly Channel<SessionEvent> _events =
        Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingApprovals = new();
    private readonly ConcurrentDictionary<string, PendingQuestion> _pendingQuestions = new();
    private readonly IReadOnlyDictionary<string, CodexCollaborationModeMask> _collaborationModes;
    private readonly string _model;
    private CodexCollaborationMode? _collaborationMode;
    private TaskCompletionSource<StopReason>? _activeTurn;

    internal const string GoalCommandId = "codex.thread.goal.set";
    internal const string ClearGoalCommandId = "codex.thread.goal.clear";

    public CodexAgentSession(
        string threadId, ICodexRpc rpc, ILogger logger, ReasoningEffortCapability? reasoningEffort = null,
        CodexDiscoveredCapabilities? discovered = null, string? model = null)
    {
        AgentSessionId = threadId;
        _rpc = rpc;
        _logger = logger;
        ReasoningEffort = reasoningEffort;
        _model = model ?? string.Empty;
        ProviderGoal = ToProviderGoal(discovered?.Goal);
        _collaborationModes = (discovered?.CollaborationModes ?? [])
            .Where(m => !string.IsNullOrWhiteSpace(m.Mode) && !string.IsNullOrWhiteSpace(m.Name))
            .GroupBy(m => ModeCommandId(m.Mode!), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var commands = new List<AgentCommandInfo>();
        if (discovered?.GoalsSupported == true)
        {
            commands.Add(new AgentCommandInfo(
                GoalCommandId, "goal", "Set the persistent Codex thread goal", "objective",
                AcceptsArguments: true));
            commands.Add(new AgentCommandInfo(
                ClearGoalCommandId, "goal-clear", "Clear the persistent Codex thread goal"));
        }

        commands.AddRange(_collaborationModes.Select(pair => new AgentCommandInfo(
            pair.Key, SlashName(pair.Value.Name), $"Switch to {pair.Value.Name} mode")));
        Commands = commands;
        Modes = _collaborationModes.Values
            .Select(m => new SessionMode(m.Mode!, m.Name))
            .ToArray();
    }

    public string AgentSessionId { get; }

    public ChannelReader<SessionEvent> Events => _events.Reader;

    public ReasoningEffortCapability? ReasoningEffort { get; private set; }

    public IReadOnlyList<AgentCommandInfo> Commands { get; }

    public ProviderGoalInfo? ProviderGoal { get; private set; }

    public IReadOnlyList<SessionMode> Modes { get; }

    public string? CurrentModeId { get; private set; }

    public async Task<StopReason> PromptAsync(IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<StopReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        _activeTurn = tcs;
        try
        {
            await _rpc.StartTurnAsync(
                AgentSessionId, CodexMap.ToInput(content), ReasoningEffort?.CurrentEffortId,
                _collaborationMode, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _activeTurn = null;
            throw;
        }

        await using (cancellationToken.Register(() => tcs.TrySetResult(StopReason.Cancelled)))
        {
            return await tcs.Task.ConfigureAwait(false);
        }
    }

    public Task CancelAsync(CancellationToken cancellationToken = default) => _rpc.InterruptAsync(AgentSessionId);

    public Task SetReasoningEffortAsync(string effortId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var capability = ReasoningEffort
            ?? throw new NotSupportedException("This Codex session has no reasoning-effort capability.");
        if (_activeTurn is not null && !_activeTurn.Task.IsCompleted)
        {
            throw new InvalidOperationException("Reasoning effort cannot be changed while a turn is active.");
        }

        if (!capability.SupportedValues.Any(v => string.Equals(v.Id, effortId, StringComparison.Ordinal)))
        {
            throw new ArgumentException(UnsupportedEffortMessage(effortId, capability.SupportedValues), nameof(effortId));
        }

        ReasoningEffort = capability with { CurrentEffortId = effortId };
        return Task.CompletedTask;
    }

    public Task SetModeAsync(string modeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = _collaborationModes.Values.FirstOrDefault(
            m => string.Equals(m.Mode, modeId, StringComparison.Ordinal))
            ?? throw new ArgumentException($"Codex did not advertise collaboration mode '{modeId}'.", nameof(modeId));
        var effectiveModel = entry.Model ?? _model;
        _collaborationMode = new CodexCollaborationMode(
            entry.Mode!,
            new CodexCollaborationSettings(
                effectiveModel, entry.ReasoningEffort ?? ReasoningEffort?.CurrentEffortId));
        CurrentModeId = entry.Mode;
        Emit(new ModeChangedEvent(entry.Mode!));
        return Task.CompletedTask;
    }

    public async Task ExecuteCommandAsync(
        string commandId, string? argument, CancellationToken cancellationToken = default)
    {
        if (string.Equals(commandId, GoalCommandId, StringComparison.Ordinal))
        {
            var objective = argument?.Trim();
            if (string.IsNullOrWhiteSpace(objective))
            {
                throw new ArgumentException("/goal requires an objective.", nameof(argument));
            }

            var goal = await _rpc.SetGoalAsync(AgentSessionId, objective, cancellationToken).ConfigureAwait(false);
            UpdateProviderGoal(ToProviderGoal(goal));
            return;
        }

        if (string.Equals(commandId, ClearGoalCommandId, StringComparison.Ordinal))
        {
            if (await _rpc.ClearGoalAsync(AgentSessionId, cancellationToken).ConfigureAwait(false))
            {
                UpdateProviderGoal(null);
            }
            return;
        }

        if (_collaborationModes.TryGetValue(commandId, out var mode))
        {
            await SetModeAsync(mode.Mode!, cancellationToken).ConfigureAwait(false);
            return;
        }

        throw new NotSupportedException($"Codex command '{commandId}' was not discovered for this session.");
    }

    internal static string UnsupportedEffortMessage(string effortId, IReadOnlyList<ReasoningEffortInfo> supported)
        => $"Reasoning effort '{effortId}' is not supported. Valid values: {string.Join(", ", supported.Select(v => v.Id))}.";

    public Task RespondToPermissionAsync(string requestId, string optionId, CancellationToken cancellationToken = default)
    {
        if (_pendingApprovals.TryGetValue(requestId, out var pending))
        {
            pending.TrySetResult(optionId == "approve");
        }
        else
        {
            _logger.LogWarning("Codex approval response for unknown request {RequestId}", requestId);
        }

        return Task.CompletedTask;
    }

    public Task AnswerQuestionAsync(string requestId, IReadOnlyList<QuestionAnswer> answers, CancellationToken cancellationToken = default)
    {
        if (_pendingQuestions.TryGetValue(requestId, out var pending))
        {
            pending.Completion.TrySetResult(answers);
        }
        else
        {
            _logger.LogWarning("Codex question answer for unknown request {RequestId}", requestId);
        }

        return Task.CompletedTask;
    }

    // ---- called by the connection (on the serial dispatch thread) ----

    /// <summary>
    /// Handle an <c>item/tool/requestUserInput</c> server request: surface the questions to the user,
    /// wait for their answers, and return them as the RPC result. Empty answers (dismissed) are echoed
    /// back as empty selections so the turn doesn't hang.
    /// </summary>
    public async Task<CodexRequestUserInputResult> HandleUserInputAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("n");
        var toolCallId = GetString(parameters, "itemId") ?? string.Empty;
        var (questions, order) = ParseQuestions(parameters);

        // No parseable questions — answer empty rather than surfacing an empty card.
        if (questions.Count == 0)
        {
            return new CodexRequestUserInputResult(new Dictionary<string, CodexUserInputAnswer>());
        }

        var tcs = new TaskCompletionSource<IReadOnlyList<QuestionAnswer>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingQuestions[requestId] = new PendingQuestion(tcs, order);

        Emit(new QuestionAskedEvent(requestId, toolCallId, questions));

        try
        {
            await using (cancellationToken.Register(() => tcs.TrySetResult(Array.Empty<QuestionAnswer>())))
            {
                var answers = await tcs.Task.ConfigureAwait(false);
                Emit(new QuestionAnsweredEvent(requestId));
                return BuildResult(answers, order);
            }
        }
        finally
        {
            _pendingQuestions.TryRemove(requestId, out _);
        }
    }

    /// <summary>Parse Codex's <c>questions[]</c> into Agnes questions, keeping each question's id so the
    /// answer result can be keyed back. Codex answers are always arrays, so questions are single-select by
    /// default (the array carries one label); <c>isOther</c> means free-text notes are accepted.</summary>
    private static (IReadOnlyList<AgentQuestion> Questions, IReadOnlyList<string> Order) ParseQuestions(JsonElement p)
    {
        var questions = new List<AgentQuestion>();
        var order = new List<string>();
        if (!p.TryGetProperty("questions", out var qs) || qs.ValueKind != JsonValueKind.Array)
        {
            return (questions, order);
        }

        var index = 0;
        foreach (var q in qs.EnumerateArray())
        {
            var id = GetString(q, "id") ?? $"q{index}";
            var header = GetString(q, "header") ?? string.Empty;
            var prompt = GetString(q, "question") ?? header;
            var allowFreeText = q.TryGetProperty("isOther", out var other) && other.ValueKind == JsonValueKind.True;

            var options = new List<QuestionChoice>();
            if (q.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
            {
                foreach (var o in opts.EnumerateArray())
                {
                    var label = GetString(o, "label") ?? string.Empty;
                    if (label.Length == 0)
                    {
                        continue;
                    }

                    options.Add(new QuestionChoice(label, GetString(o, "description") ?? string.Empty));
                }
            }

            questions.Add(new AgentQuestion(id, header, prompt, options, MultiSelect: false, AllowFreeText: allowFreeText || options.Count == 0));
            order.Add(id);
            index++;
        }

        return (questions, order);
    }

    /// <summary>Map the user's answers into Codex's result shape: <c>{answers:{[id]:{answers:string[]}}}</c>.
    /// Free-text notes ride as an extra answer string (Codex's "Other" convention).</summary>
    private static CodexRequestUserInputResult BuildResult(IReadOnlyList<QuestionAnswer> answers, IReadOnlyList<string> order)
    {
        var byId = answers.ToDictionary(a => a.QuestionId, a => a);
        var result = new Dictionary<string, CodexUserInputAnswer>();
        foreach (var id in order)
        {
            var picks = new List<string>();
            if (byId.TryGetValue(id, out var a))
            {
                picks.AddRange(a.SelectedLabels);
                if (!string.IsNullOrWhiteSpace(a.Notes))
                {
                    picks.Add(a.Notes.Trim());
                }
            }

            result[id] = new CodexUserInputAnswer(picks);
        }

        return new CodexRequestUserInputResult(result);
    }

    private sealed record PendingQuestion(TaskCompletionSource<IReadOnlyList<QuestionAnswer>> Completion, IReadOnlyList<string> Order);

    public void HandleItemStarted(JsonElement notification)
    {
        foreach (var e in _map.ItemStarted(notification))
        {
            Emit(e);
        }
    }

    public void HandleItemCompleted(JsonElement notification)
    {
        foreach (var e in _map.ItemCompleted(notification))
        {
            Emit(e);
        }
    }

    public void HandleAgentMessageDelta(JsonElement notification)
    {
        if (_map.AgentMessageDelta(notification) is { } e)
        {
            Emit(e);
        }
    }

    public void HandleTokenUsage(JsonElement notification)
    {
        if (_map.TokenUsage(notification) is { } e)
        {
            Emit(e);
        }
    }

    public void HandleGoalUpdated(JsonElement notification)
    {
        if (notification.TryGetProperty("goal", out var value)
            && value.Deserialize<CodexGoal>(CodexJson.Read) is { } goal)
        {
            UpdateProviderGoal(ToProviderGoal(goal));
        }
    }

    public void HandleGoalCleared(JsonElement notification) => UpdateProviderGoal(null);

    public void HandleError(JsonElement notification)
    {
        var message = notification.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var m)
            && m.ValueKind == JsonValueKind.String
            ? m.GetString()
            : "Codex reported an error.";
        Emit(new AgentErrorEvent(message ?? "Codex reported an error."));
    }

    public void HandleTurnCompleted(JsonElement notification)
    {
        var status = notification.TryGetProperty("turn", out var turn) && turn.TryGetProperty("status", out var s)
            && s.ValueKind == JsonValueKind.String
            ? s.GetString()
            : null;
        var reason = CodexMap.ToStopReason(status);
        Emit(new TurnEndedEvent(reason));
        _activeTurn?.TrySetResult(reason);
        _activeTurn = null;
    }

    public async Task<CodexApprovalResponse> HandleApprovalAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("n");
        var toolCallId = GetString(parameters, "callId") ?? string.Empty;
        var title = ApprovalTitle(parameters);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingApprovals[requestId] = tcs;

        Emit(new PermissionRequestedEvent(requestId, toolCallId, title,
        [
            new PermissionOption("approve", "Approve", PermissionOptionKind.AllowOnce),
            new PermissionOption("deny", "Deny", PermissionOptionKind.RejectOnce),
        ], ApprovalDetail(parameters)));

        try
        {
            await using (cancellationToken.Register(() => tcs.TrySetResult(false)))
            {
                var allow = await tcs.Task.ConfigureAwait(false);
                Emit(new PermissionResolvedEvent(requestId, allow ? "approve" : "deny",
                    allow ? PermissionOutcome.Allowed : PermissionOutcome.Denied));
                return new CodexApprovalResponse(CodexMap.Decision(allow));
            }
        }
        finally
        {
            _pendingApprovals.TryRemove(requestId, out _);
        }
    }

    private static string ApprovalTitle(JsonElement p)
        => GetString(p, "reason") is { Length: > 0 } reason
            ? reason
            : ApprovalCommand(p) is { } command ? $"Run: {command}" : "Approval required";

    /// <summary>The command being approved, verbatim, whether or not Codex also gave a reason — the reason
    /// used to displace it from the title, leaving nothing to actually review.</summary>
    private static string? ApprovalDetail(JsonElement p) => ApprovalCommand(p);

    // A command is a string, or an argv array — the same polymorphic field CodexMap handles.
    private static string? ApprovalCommand(JsonElement p)
    {
        if (!p.TryGetProperty("command", out var c))
        {
            return null;
        }

        return c.ValueKind switch
        {
            JsonValueKind.String => c.GetString(),
            JsonValueKind.Array => string.Join(' ', c.EnumerateArray().Select(e => e.GetString())),
            _ => null,
        };
    }

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static string ModeCommandId(string modeId) => $"codex.collaboration-mode.{modeId}";

    private static string SlashName(string name)
    {
        var chars = name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        return new string(chars).Trim('-');
    }

    private static ProviderGoalInfo? ToProviderGoal(CodexGoal? goal)
        => goal is null ? null : new ProviderGoalInfo(
            goal.Objective, goal.Status, goal.TokenBudget, goal.TokensUsed, goal.TimeUsedSeconds);

    private void UpdateProviderGoal(ProviderGoalInfo? goal)
    {
        if (Equals(ProviderGoal, goal))
        {
            return;
        }

        ProviderGoal = goal;
        Emit(new ProviderGoalChangedEvent(goal));
    }

    private void Emit(SessionEvent e)
    {
        if (!_events.Writer.TryWrite(e with { Timestamp = DateTimeOffset.UtcNow }))
        {
            _logger.LogWarning("Dropped Codex event for thread {ThreadId}", AgentSessionId);
        }
    }

    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        _activeTurn?.TrySetResult(StopReason.Cancelled);
        foreach (var pending in _pendingApprovals.Values)
        {
            pending.TrySetResult(false);
        }

        return ValueTask.CompletedTask;
    }
}
