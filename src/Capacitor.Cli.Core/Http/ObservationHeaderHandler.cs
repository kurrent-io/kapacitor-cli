namespace Capacitor.Cli.Core.Http;

/// <summary>
/// Tags every request with the headers the server's update-notification pipeline reads.
///
/// <para>Takes the two values rather than resolving them: both are fixed for the process, and a
/// handler that reads a profile for itself cannot be registered by a host that has none.</para>
/// </summary>
internal sealed class ObservationHeaderHandler(string? version, bool updateCheckOff) : DelegatingHandler {
    /// <summary>
    /// The version tag this process sends, or null when it cannot name a real one — "unknown" on the
    /// wire is worse than an absent header, since the pipeline reads it as a version.
    /// </summary>
    public static string? ProcessVersion { get; } = Usable(CapacitorVersion.CurrentDisplay());

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
        if (version is not null) request.Headers.TryAddWithoutValidation(HttpClientExtensions.CliVersionHeader, version);

        // Sent ONLY to declare the preference is off; its absence means on.
        if (updateCheckOff)
            request.Headers.TryAddWithoutValidation(
                HttpClientExtensions.UpdateCheckHeader, HttpClientExtensions.UpdateCheckOffValue);

        return base.SendAsync(request, ct);
    }

    static string? Usable(string? version) =>
        !string.IsNullOrWhiteSpace(version) && !version.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            ? version
            : null;
}
