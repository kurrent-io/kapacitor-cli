namespace Capacitor.Cli.Core.Http;

/// <summary>
/// The call produced no result: the server refused it, answered with something unexpected, or was
/// never reached. <c>Message</c> is already phrased for a user, so a caller need only write it out.
/// <c>Status</c> is null when no response arrived at all — the cause is then the inner exception.
/// </summary>
public sealed class CapacitorApiException(int? status, string message, Exception? inner = null)
        : Exception(message, inner) {
    public int? Status { get; } = status;
}
