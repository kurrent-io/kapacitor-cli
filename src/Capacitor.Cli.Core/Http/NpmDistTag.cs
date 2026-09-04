namespace Capacitor.Cli.Core.Http;

/// <summary>
/// What a registry said about a dist-tag. <c>Reached</c> false means the query did not produce an
/// answer — network, timeout, non-2xx or an unreadable body — and the caller arms a backoff. A
/// registry that answered but named no version is not that case and must not arm one.
/// </summary>
public readonly record struct NpmDistTag(bool Reached, string? Version);
