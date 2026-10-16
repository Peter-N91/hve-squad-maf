using System.Text.Json;

namespace HveSquad.AgentFramework.Runtime;

/// <summary>The exact, non-secret identity of one pending human action.</summary>
public sealed record SquadHumanActionBinding(
    string Id, string RunId, string RootSha256, string ReleaseFingerprint, string HostBinding,
    int Revision, string Gate, string Step, string? Role, string? DispatchId, string? RoundId,
    string ActionSha256, string EvidenceSha256, string PayloadSha256, string Mode = "interactive",
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? FederationRunId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? FederationMember = null);

/// <summary>A notification-safe view. Private requests, answers and tool arguments are never included.</summary>
public sealed record SquadPendingHumanAction(
    SquadHumanActionBinding Binding, bool IsQuestion, string Channel,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string Status,
    string DeliveryStatus = "not-sent", string? Reference = null, string? Diagnostic = null,
    string OutboundStatus = "not-enabled", string? OutboundDiagnostic = null);

/// <summary>Host-only details read from protected case storage; never an outbound notification.</summary>
public sealed record SquadHumanActionDetails(SquadPendingHumanAction Action,
    SquadApprovalRequest? Approval, SquadQuestion? Question);

/// <summary>Recognized human control signals. An answer is context, not approval.</summary>
public enum SquadHumanDecision
{
    /// <summary>Approve only the unchanged identified action.</summary>
    Approve,
    /// <summary>Answer only the identified question.</summary>
    Answer,
    /// <summary>Reopen affected work with descriptive input, not executable instructions.</summary>
    Changes,
    /// <summary>Stop safely without replaying or concealing uncertain effects.</summary>
    Stop,
}

/// <summary>An untrusted submission until its registered channel authenticates the actual actor and source.</summary>
public sealed record SquadHumanResponse(
    SquadHumanActionBinding Binding, SquadHumanDecision Decision, string ResponseId,
    string? Input = null, DateTimeOffset? CreatedAt = null)
{
    /// <summary>Parses an exact control command; unrelated prose and blanket approvals are rejected.</summary>
    /// <param name="action">The exact action displayed to the human.</param>
    /// <param name="command">An approve, answer, changes or stop command.</param>
    /// <param name="responseId">A non-secret authenticated-channel response reference.</param>
    /// <param name="createdAt">The channel's actual response time, when available.</param>
    /// <returns>The still-unverified submission.</returns>
    public static SquadHumanResponse FromCommand(SquadPendingHumanAction action, string command,
        string responseId, DateTimeOffset? createdAt = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        command = command.Trim();
        var (decision, input) = command switch
        {
            "/approve" => (SquadHumanDecision.Approve, (string?)null),
            "/stop" => (SquadHumanDecision.Stop, null),
            _ when command.StartsWith("/changes:", StringComparison.Ordinal) =>
                (SquadHumanDecision.Changes, command["/changes:".Length..].Trim()),
            _ when command.StartsWith("/answer:", StringComparison.Ordinal) =>
                (SquadHumanDecision.Answer, command["/answer:".Length..].Trim()),
            _ => throw new ArgumentException("Use exactly /approve, /answer: <answer>, /changes: <note>, or /stop.", nameof(command)),
        };
        if (decision is SquadHumanDecision.Answer or SquadHumanDecision.Changes && string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException("Answers and change requests require nonempty descriptive input.", nameof(command));
        }
        return new(action.Binding, decision, responseId, input, createdAt);
    }
}

/// <summary>Non-secret actor attestation from host authentication, not from submitted text.</summary>
public sealed record SquadHumanAuthorization(string Provider, string SubjectSha256);

/// <summary>Actual delivery evidence. Uncertain delivery is never an approval.</summary>
public sealed record SquadHumanDelivery(string Status, string? Reference = null, string? Diagnostic = null);

/// <summary>One accepted or explicitly rejected submission; accepting a response executes no model or project effect.</summary>
public sealed record SquadHumanSubmission(bool Accepted, string ActionId, SquadRunStatus RunStatus, string Diagnostic);

/// <summary>One bounded channel poll, including ignored/rejected responses and missing-response diagnostics.</summary>
public sealed record SquadHumanPoll(IReadOnlyList<SquadHumanSubmission> Submissions, IReadOnlyList<string> Diagnostics);

/// <summary>
/// A trusted host channel. Implementations must authenticate the actual session or re-read the
/// authenticated provider event. Submitted actor names, issue bodies and local hashes are not authority.
/// </summary>
public interface ISquadHumanApprovalChannel
{
    /// <summary>In-chat or github-issue; webhook is deliberately not an approval channel.</summary>
    string Name { get; }
    /// <summary>Stable non-secret registration/configuration identity; credentials may rotate independently.</summary>
    string Binding { get; }
    /// <summary>Publishes only the notification-safe view, with recoverable delivery evidence.</summary>
    ValueTask<SquadHumanDelivery> PublishAsync(SquadPendingHumanAction action, CancellationToken cancellationToken);
    /// <summary>Reads bounded candidate responses. Each still requires separate authentication before acceptance.</summary>
    ValueTask<IReadOnlyList<SquadHumanResponse>> ReadResponsesAsync(SquadPendingHumanAction action, CancellationToken cancellationToken);
    /// <summary>Verifies the actual response and authorized human account; null explicitly denies authorization.</summary>
    ValueTask<SquadHumanAuthorization?> AuthorizeAsync(SquadPendingHumanAction action,
        SquadHumanResponse response, CancellationToken cancellationToken);
    /// <summary>Records the non-secret outcome and closes/relabels its remote issue when applicable.</summary>
    ValueTask<SquadHumanDelivery> ResolveAsync(SquadPendingHumanAction action,
        SquadHumanDecision decision, CancellationToken cancellationToken);
}

/// <summary>An outbound-only host notifier. No implementation of this interface can approve anything.</summary>
public interface ISquadHumanNotifier
{
    /// <summary>Opaque non-secret host registration key, never an endpoint.</summary>
    string Binding { get; }
    /// <summary>Delivers only redacted pending metadata; acknowledgement is not approval.</summary>
    ValueTask<SquadHumanDelivery> PublishAsync(SquadPendingHumanAction action, CancellationToken cancellationToken);
}

/// <summary>A transport failure with a safe diagnostic, deliberately excluding endpoints, headers and response bodies.</summary>
public sealed class SquadHumanTransportException(string message, bool outcomeUncertain = false) : Exception(message)
{
    /// <summary>Whether a remote mutation may have succeeded without a verified response.</summary>
    public bool OutcomeUncertain { get; } = outcomeUncertain;
}

internal sealed record HumanActionPayload(SquadApprovalRequest? Approval, SquadQuestion? Question,
    IReadOnlyList<string>? CostSlots = null);

internal sealed class HumanActionCheckpoint
{
    public required SquadPendingHumanAction Action { get; set; }
    public required string PayloadPath { get; init; }
    public required string Scope { get; init; }
    public HumanDecisionReceipt? Decision { get; set; }
    public string? ResolutionStatus { get; set; }
}

internal sealed record HumanDecisionReceipt(
    SquadHumanActionBinding Binding, SquadHumanDecision Decision, string ResponseId,
    string Provider, string SubjectSha256, DateTimeOffset RecordedAt,
    string? InputPath = null, string? InputSha256 = null);

internal sealed record HumanChangeInput(string ActionId, string Step, string? DispatchId,
    string InputPath, string InputSha256);

internal static class HumanActions
{
    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value);
    internal static string Hash<T>(T value) => ProjectFiles.Hash(Serialize(value));
    internal static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    internal static bool IsReference(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or ':' or '.');
    internal static SquadPendingHumanAction Copy(SquadPendingHumanAction action) => action with { Binding = action.Binding with { } };
}
