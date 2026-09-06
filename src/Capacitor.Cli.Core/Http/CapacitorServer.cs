using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Http;

/// <summary>
/// The one Capacitor server this process talks to, plus the context reaching it needs. A handler the
/// factory constructs cannot be handed per-call arguments, so this carries them as a singleton.
/// </summary>
public sealed class CapacitorServer(string? url, ConfigRoot config, ProfileContext profiles) {
    /// <summary>
    /// Without a trailing slash, because every caller builds a path by interpolating onto it: a
    /// configured <c>https://host/</c> would otherwise reach the server as <c>//api/...</c>. Empty
    /// when nothing resolved one, which <see cref="Usable"/> already refuses.
    /// </summary>
    public string Url { get; } = url?.TrimEnd('/') ?? "";

    public ConfigRoot     Config   { get; } = config;
    public ProfileContext Profiles { get; } = profiles;

    /// <summary>
    /// Whether <see cref="Url"/> can be sent to at all. Evaluated once here rather than per request:
    /// the answer cannot change within a process, and a send path that re-asks has to decide what to
    /// do about it mid-request, which is how validation ends up ending the process.
    /// </summary>
    public bool Usable { get; } = HookHttp.IsPostable(url);
}
