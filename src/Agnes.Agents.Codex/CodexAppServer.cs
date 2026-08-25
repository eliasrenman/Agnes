using System.Diagnostics;
using System.Threading.Channels;
using Agnes.Abstractions;
using Agnes.Agents.Codex.Wire;
using Microsoft.Extensions.Logging;

namespace Agnes.Agents.Codex;

/// <summary>Launch descriptor for the Codex app-server adapter.</summary>
public sealed record CodexLaunchSpec
{
    public string Command { get; init; } = "codex";
    public IReadOnlyList<string> Arguments { get; init; } = ["app-server"];
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
    public AgentDescriptor Descriptor { get; init; } = CodexAppServer.Descriptor;
}

/// <summary>
/// A native Codex adapter that drives <c>codex app-server</c> — Codex's persistent JSON-RPC stdio
/// server — over one long-lived process per session. Unlike <c>codex exec</c> (single-turn), the
/// app-server keeps a thread alive across turns, matching Agnes's persistent-session model. Streams
/// map through <see cref="CodexMap"/>; approvals surface as permission requests.
/// </summary>
public sealed class CodexAppServerAdapter : IAgentAdapter, IModelListingAdapter
{
    private readonly CodexLaunchSpec _spec;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Func<CancellationToken, Task<IReadOnlyList<CodexModel>>>? _modelProbeOverride;

    public CodexAppServerAdapter(CodexLaunchSpec spec, ILoggerFactory loggerFactory)
        : this(spec, loggerFactory, modelProbeOverride: null)
    {
    }

    internal CodexAppServerAdapter(
        CodexLaunchSpec spec,
        ILoggerFactory loggerFactory,
        Func<CancellationToken, Task<IReadOnlyList<CodexModel>>>? modelProbeOverride)
    {
        _spec = spec;
        _loggerFactory = loggerFactory;
        _modelProbeOverride = modelProbeOverride;
    }

    public AgentDescriptor Descriptor => _spec.Descriptor;

    public bool IsAvailable() => AgentCommand.IsOnPath(_spec.Command);

    /// <summary>Codex's selectable models. The app-server also accepts other ids the installed CLI knows, so
    /// custom entry stays enabled — the picker isn't authoritative, it's a convenience over the common ones.</summary>
    public IReadOnlyList<ModelInfo> StaticModels { get; } =
    [
        new ModelInfo("gpt-5-codex", "GPT-5 Codex"),
        new ModelInfo("gpt-5", "GPT-5"),
    ];

    public async Task<IReadOnlyList<ModelInfo>?> ListModelsAsync(CancellationToken ct = default)
    {
        CodexConnection? connection = null;
        try
        {
            if (_modelProbeOverride is not null)
            {
                return (await _modelProbeOverride(ct).ConfigureAwait(false)).Select(ToModelInfo).ToArray();
            }

            var options = new AgentSessionOptions { WorkingDirectory = Environment.CurrentDirectory };
            var process = StartProcess(options);
            connection = new CodexConnection(
                process.StandardInput.BaseStream,
                process.StandardOutput.BaseStream,
                _loggerFactory.CreateLogger<CodexConnection>(),
                new ProcessLifetime(process, _loggerFactory.CreateLogger<CodexAppServerAdapter>()));
            await connection.InitializeAsync(ct).ConfigureAwait(false);
            return (await connection.ListModelsAsync(ct).ConfigureAwait(false)).Select(ToModelInfo).ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _loggerFactory.CreateLogger<CodexAppServerAdapter>()
                .LogWarning(ex, "Codex model discovery failed; using the static fallback without effort metadata");
            return null;
        }
        finally
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async Task<IAgentSession> StartSessionAsync(AgentSessionOptions options, CancellationToken cancellationToken = default)
    {
        var process = StartProcess(options);
        var lifetime = new ProcessLifetime(process, _loggerFactory.CreateLogger<CodexAppServerAdapter>());
        var connection = new CodexConnection(
            process.StandardInput.BaseStream,
            process.StandardOutput.BaseStream,
            _loggerFactory.CreateLogger<CodexConnection>(),
            lifetime);
        try
        {
            await connection.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var liveModels = await connection.ListModelsAsync(cancellationToken).ConfigureAwait(false);
            var selectedModel = SelectModel(liveModels, options.ModelId);
            var reasoning = CreateReasoningCapability(selectedModel, options.ReasoningEffortId);

            // Ask-per-tool by default (Codex sends approval requests); autonomous opts out of prompts.
            var approvalPolicy = options.SkipPermissions ? "never" : "on-request";
            var sandbox = options.SkipPermissions && options.Sandbox is not null ? "danger-full-access" : "workspace-write";

            var effectiveModel = options.ModelId ?? selectedModel?.Id;
            var session = await connection.StartThreadAsync(
                options.WorkingDirectory, approvalPolicy, sandbox, effectiveModel, cancellationToken, reasoning).ConfigureAwait(false);

            // Disposing the session tears down the connection (and kills the app-server process).
            return new ConnectionOwningSession(session, connection);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static CodexModel? SelectModel(IReadOnlyList<CodexModel> models, string? modelId)
        => string.IsNullOrWhiteSpace(modelId)
            ? models.FirstOrDefault(m => m.IsDefault) ?? models.FirstOrDefault()
            : models.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.Ordinal)
                || string.Equals(m.Model, modelId, StringComparison.Ordinal));

    internal static ReasoningEffortCapability? CreateReasoningCapability(CodexModel? model, string? requestedEffort)
    {
        if (model is null)
        {
            if (!string.IsNullOrWhiteSpace(requestedEffort))
            {
                throw new ArgumentException(
                    $"Codex did not report metadata for the selected model, so reasoning effort '{requestedEffort}' cannot be validated.",
                    nameof(requestedEffort));
            }

            return null;
        }

        var supported = model.SupportedReasoningEfforts.Select(ToReasoningEffortInfo).ToArray();
        if (supported.Length == 0)
        {
            if (!string.IsNullOrWhiteSpace(requestedEffort))
            {
                throw new ArgumentException($"Model '{model.Id}' does not support configurable reasoning effort.", nameof(requestedEffort));
            }

            return null;
        }

        var effective = string.IsNullOrWhiteSpace(requestedEffort) ? model.DefaultReasoningEffort : requestedEffort;
        if (effective is null || supported.All(v => !string.Equals(v.Id, effective, StringComparison.Ordinal)))
        {
            throw new ArgumentException(CodexAgentSession.UnsupportedEffortMessage(effective ?? "<default>", supported), nameof(requestedEffort));
        }

        return new ReasoningEffortCapability(supported, model.DefaultReasoningEffort, effective, SupportsRuntimeChanges: true);
    }

    internal static ModelInfo ToModelInfo(CodexModel model)
        => new(
            model.Id,
            model.DisplayName,
            IsCustomEntryAllowed: true,
            model.SupportedReasoningEfforts.Select(ToReasoningEffortInfo).ToArray(),
            model.DefaultReasoningEffort);

    private static ReasoningEffortInfo ToReasoningEffortInfo(CodexReasoningEffortOption effort)
        => new(effort.ReasoningEffort, DisplayEffort(effort.ReasoningEffort), effort.Description);

    private static string DisplayEffort(string value)
        => string.Join(' ', value.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    private Process StartProcess(AgentSessionOptions options)
    {
        // When a sandbox is set, run the agent inside it (streams flow through the exec pipe). The
        // guest working directory travels in thread/start's cwd, so the host launcher uses a real
        // host directory, not the guest path.
        var (command, arguments) = (_spec.Command, (IReadOnlyList<string>)_spec.Arguments);
        var hostWorkingDirectory = options.WorkingDirectory;
        if (options.Sandbox is { } sandbox)
        {
            (command, arguments) = sandbox.WrapCommand(command, arguments, options.WorkingDirectory);
            hostWorkingDirectory = Environment.CurrentDirectory;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            WorkingDirectory = hostWorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        ApplyEnvironment(startInfo, _spec.Environment);
        ApplyEnvironment(startInfo, options.Environment);

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start Codex app-server '{_spec.Command}'.");
    }

    private static void ApplyEnvironment(ProcessStartInfo startInfo, IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is null)
        {
            return;
        }

        foreach (var (key, value) in environment)
        {
            startInfo.Environment[key] = value;
        }
    }

    /// <summary>Wraps the session so disposing it also disposes the owning connection (and process).</summary>
    internal sealed class ConnectionOwningSession(IAgentSession inner, IAsyncDisposable owner) : IAgentSession
    {
        public string AgentSessionId => inner.AgentSessionId;
        public ChannelReader<SessionEvent> Events => inner.Events;
        public ReasoningEffortCapability? ReasoningEffort => inner.ReasoningEffort;
        public IReadOnlyList<SessionMode> Modes => inner.Modes;
        public string? CurrentModeId => inner.CurrentModeId;
        public IReadOnlyList<AgentCommandInfo> Commands => inner.Commands;
        public ProviderGoalInfo? ProviderGoal => inner.ProviderGoal;

        public Task<StopReason> PromptAsync(IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken = default)
            => inner.PromptAsync(content, cancellationToken);

        public Task CancelAsync(CancellationToken cancellationToken = default) => inner.CancelAsync(cancellationToken);

        public Task RespondToPermissionAsync(string requestId, string optionId, CancellationToken cancellationToken = default)
            => inner.RespondToPermissionAsync(requestId, optionId, cancellationToken);

        public Task AnswerQuestionAsync(string requestId, IReadOnlyList<QuestionAnswer> answers, CancellationToken cancellationToken = default)
            => inner.AnswerQuestionAsync(requestId, answers, cancellationToken);

        public Task SetReasoningEffortAsync(string effortId, CancellationToken cancellationToken = default)
            => inner.SetReasoningEffortAsync(effortId, cancellationToken);

        public Task SetModeAsync(string modeId, CancellationToken cancellationToken = default)
            => inner.SetModeAsync(modeId, cancellationToken);

        public Task ExecuteCommandAsync(string commandId, string? argument, CancellationToken cancellationToken = default)
            => inner.ExecuteCommandAsync(commandId, argument, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await owner.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Pumps stderr to the log and kills the process tree on dispose.</summary>
    private sealed class ProcessLifetime : IAsyncDisposable
    {
        private readonly Process _process;

        public ProcessLifetime(Process process, ILogger logger)
        {
            _process = process;
            _ = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    logger.LogDebug("[codex stderr] {Line}", line);
                }
            });
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // already gone
            }

            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Registers the native Codex (app-server) adapter — mirrors <c>ClaudeCodeNative</c>.</summary>
public static class CodexAppServer
{
    public const string AdapterId = "codex";

    public static readonly AgentDescriptor Descriptor = new()
    {
        Id = AdapterId,
        DisplayName = "Codex",
    };

    /// <summary>The config override that turns on Codex's structured "ask the user" questions
    /// (<c>item/tool/requestUserInput</c>). The flag is "under development" upstream — opt-in only.</summary>
    internal const string UserInputConfigArg = "features.default_mode_request_user_input=true";

    /// <param name="enableUserInput">Prepend <c>-c features.default_mode_request_user_input=true</c> so the
    /// app-server surfaces structured questions. Experimental — off by default.</param>
    public static CodexAppServerAdapter Create(
        ILoggerFactory loggerFactory,
        string? command = null,
        IReadOnlyList<string>? arguments = null,
        bool enableUserInput = false)
    {
        var args = arguments ?? ["app-server"];
        if (enableUserInput && !args.Any(a => a.Contains("default_mode_request_user_input", StringComparison.Ordinal)))
        {
            args = ["-c", UserInputConfigArg, .. args];
        }

        return new(new CodexLaunchSpec
        {
            Command = command ?? "codex",
            Arguments = args,
            Descriptor = Descriptor,
        }, loggerFactory);
    }
}
