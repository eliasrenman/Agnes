namespace Agnes.Abstractions.Events;

/// <summary>Dispatched before a provider-native command is executed.</summary>
public sealed class BeforeAgentCommandExecuteEvent(
    string sessionId, string commandId, string? argument) : CancelableEvent
{
    public string SessionId { get; } = sessionId;
    public string CommandId { get; set; } = commandId;
    public string? Argument { get; set; } = argument;
}
