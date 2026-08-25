namespace Agnes.Abstractions.Events;

/// <summary>Vetoable request to change a session's provider-specific reasoning effort.</summary>
public sealed class BeforeReasoningEffortChangeEvent(string sessionId, string effortId) : CancelableEvent
{
    public string SessionId { get; } = sessionId;
    public string EffortId { get; set; } = effortId;
}
