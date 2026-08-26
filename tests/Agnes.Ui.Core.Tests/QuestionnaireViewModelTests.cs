using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core.Transcript;
using Agnes.Ui.Core.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.Ui.Core.Tests;

public sealed class QuestionnaireViewModelTests
{
    private sealed class Host : StubAgnesHost
    {
        public TaskCompletionSource? Gate { get; set; }
        public Exception? Failure { get; set; }
        public IReadOnlyList<QuestionAnswer>? Answers { get; private set; }

        public override Task AnswerQuestionAsync(
            string sessionId,
            string requestId,
            IReadOnlyList<QuestionAnswer> answers)
        {
            Answers = answers;
            return Failure is { } failure ? Task.FromException(failure)
                : Gate?.Task ?? Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Submit_immediately_shows_progress_then_resolves_without_waiting_for_provider_echo()
    {
        var host = new Host { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var (view, vm, item) = Subject(host);
        item.Questions[0].Options[0].IsSelected = true;

        var operation = ((IAsyncRelayCommand)vm.AnswerQuestionCommand).ExecuteAsync(item);

        Assert.True(item.IsSubmitting);
        Assert.False(item.CanRespond);
        Assert.Equal("Submitting answers…", item.SubmissionText);
        Assert.Equal(SessionActivity.Running, vm.Activity);
        Assert.Equal(["Recommended"], Assert.Single(host.Answers!).SelectedLabels);
        Assert.Same(item, vm.PendingQuestion);

        host.Gate.SetResult();
        await operation;

        Assert.True(item.Resolved);
        Assert.False(item.IsSubmitting);
        Assert.Equal("Answers submitted", item.ResolutionText);
        Assert.Null(vm.PendingQuestion);

        // The provider acknowledgement may arrive afterward. It reconciles idempotently and does not
        // replace the more useful local outcome text with a generic historical label.
        view.Apply(new QuestionAnsweredEvent("request-1") { Sequence = 2 });
        Assert.Equal("Answers submitted", item.ResolutionText);
    }

    [Fact]
    public async Task Dismiss_is_visibly_distinct_and_sends_no_answers()
    {
        var host = new Host();
        var (_, vm, item) = Subject(host);

        await ((IAsyncRelayCommand)vm.DismissQuestionCommand).ExecuteAsync(item);

        Assert.Empty(host.Answers!);
        Assert.True(item.Resolved);
        Assert.Equal("Dismissed — continuing without answers", item.ResolutionText);
        Assert.Null(vm.PendingQuestion);
    }

    [Fact]
    public async Task Submission_failure_restores_the_controls_and_explains_the_error()
    {
        var host = new Host { Failure = new InvalidOperationException("question is no longer active") };
        var (_, vm, item) = Subject(host);

        await ((IAsyncRelayCommand)vm.AnswerQuestionCommand).ExecuteAsync(item);

        Assert.False(item.Resolved);
        Assert.False(item.IsSubmitting);
        Assert.True(item.CanRespond);
        Assert.True(item.HasSubmissionError);
        Assert.Equal(SessionActivity.NeedsInput, vm.Activity);
        Assert.Equal("question is no longer active", item.SubmissionError);
        Assert.Same(item, vm.PendingQuestion);
    }

    private static (SessionView View, SessionViewModel ViewModel, QuestionItem Item) Subject(Host host)
    {
        var question = new QuestionAskedEvent("request-1", "tool-1",
        [
            new AgentQuestion(
                "scope",
                "Scope",
                "How much should be included?",
                [new QuestionChoice("Recommended", "Use the complete behavior")]),
        ]) { Sequence = 1 };
        var view = new SessionView("session-1");
        view.ApplySnapshot(new SessionSnapshot(
            new SessionInfo("session-1", "codex", "/work", 1), [question], 1));
        var vm = new SessionViewModel(host, view, ImmediateDispatcher.Instance, "Codex");
        return (view, vm, Assert.Single(vm.Items.OfType<QuestionItem>()));
    }
}
