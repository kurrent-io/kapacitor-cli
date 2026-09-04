namespace Capacitor.Cli.Core.Auth;

/// <summary>How the stored credential relates to a 401 the server just returned.</summary>
public enum StoredCredentialState {
    /// <summary>No usable stored token — the caller genuinely is not logged in.</summary>
    Missing,

    /// <summary>A token is stored but was minted by a different server than the target.</summary>
    WrongServer,

    /// <summary>A token is stored but expired (and the recovery's refresh evidently failed).</summary>
    Expired,

    /// <summary>The stored token is unexpired and bound to the target — the server rejected a
    /// credential that looks perfectly valid locally.</summary>
    LooksValid,
}

/// <summary>
/// Builds the user-facing line for a request the server answered 401 AFTER the one-shot
/// re-read-and-refresh recovery (<c>TokenStore.RecoverForServerAsync</c>) already ran.
///
/// <para>The MCP servers used to answer every such 401 with a flat
/// "Not logged in. Run 'kcap login' on the host shell." — factually wrong in the incident
/// that motivated this: a server-side signing-key rotation 401'd a token that
/// <c>kcap status</c> concurrently (and correctly) reported as valid, so the user read the
/// two surfaces as contradicting each other, concluded <c>kcap login</c> was ineffective,
/// and reached for a daemon restart — which never helps, because every process re-reads the
/// same token store. The message now says what is actually true for each store state; the
/// legacy wording survives byte-identical for the genuinely-logged-out case.</para>
///
/// <para><b>Two renderings, one vocabulary.</b> <see cref="Render"/> is the MCP form: several
/// sentences in a tool result the model reads. The recording hooks need the same distinctions in
/// one line — a Claude <c>systemMessage</c> is a single transcript warning, and the other vendors
/// get one stderr line — so <see cref="RecordingNotice"/> renders the same
/// <see cref="StoredCredentialState"/> short. Sharing the enum rather than the prose is the point:
/// the states are the thing that must not drift, and a five-sentence paragraph in a
/// <c>systemMessage</c> would be unreadable.</para>
/// </summary>
public static class AuthRejectionNotice {
    public const string NotLoggedIn = "Not logged in. Run 'kcap login' on the host shell.";

    /// <summary>
    /// The recording surfaces' state, derived from the auth resolution they already hold — no
    /// store read. The hook path is per-turn and budget-bounded, so it must not pay the two disk
    /// reads <see cref="ForPersistentUnauthorizedAsync"/> makes just to name a state it knows.
    /// <see cref="AuthStatus.Ok"/> / <see cref="AuthStatus.NoAuthRequired"/> are not lapses and
    /// never reach a notice; they map to <see cref="StoredCredentialState.LooksValid"/> because a
    /// 401 answering a usable client is exactly that case.
    /// </summary>
    public static StoredCredentialState FromAuthStatus(AuthStatus status) =>
        status switch {
            AuthStatus.Expired          => StoredCredentialState.Expired,
            AuthStatus.NotAuthenticated => StoredCredentialState.Missing,
            AuthStatus.WrongServer      => StoredCredentialState.WrongServer,
            _                           => StoredCredentialState.LooksValid,
        };

    /// <summary>
    /// One-line form for the recording hooks. <see cref="StoredCredentialState.WrongServer"/>
    /// deliberately renders as the not-authenticated line, preserving the pre-existing hook
    /// wording — <see cref="Render"/> is the surface that names both servers.
    /// </summary>
    public static string RecordingNotice(StoredCredentialState state) =>
        state switch {
            StoredCredentialState.Expired =>
                "[kcap] Authentication expired — session recording is paused. Run 'kcap login' to resume.",

            StoredCredentialState.LooksValid =>
                "[kcap] The server rejected your credentials (HTTP 401) — session recording is paused. Run 'kcap login' to resume.",

            _ =>
                "[kcap] Not authenticated — session recording is off. Run 'kcap login' to start recording.",
        };

    /// <summary>
    /// The stderr status line for vendors with no user-facing stdout channel, for ANY response
    /// <paramref name="code"/> — not just 401, so the call sites can't drift apart on the choice
    /// between them. A 401 keeps the existing <c>[kcap] {tag} {endpoint}: HTTP 401</c> prefix — it
    /// is what the vendors' debug logs and existing issue reports show — and appends the recovery
    /// step; every other code stays the bare line unchanged.
    /// </summary>
    public static string VendorStderrLine(string agentTag, string endpoint, int code) =>
        code == 401
            ? $"[kcap] {agentTag} {endpoint}: HTTP 401 — the server rejected your credentials; run 'kcap login' to resume recording"
            : $"[kcap] {agentTag} {endpoint}: HTTP {code}";

    /// <summary>Pure classification of a raw store snapshot against the request's target server.</summary>
    public static StoredCredentialState Classify(StoredTokens? stored, string targetBaseUrl) {
        if (stored is null) return StoredCredentialState.Missing;

        // An unbound (pre-upgrade) token is treated as bound — same rule as TokenStore's
        // BoundToTarget: there is nothing to contradict.
        if (stored.ServerUrl is not null && !ServerIdentity.SameServer(stored.ServerUrl, targetBaseUrl)) {
            return StoredCredentialState.WrongServer;
        }

        return stored.IsExpired ? StoredCredentialState.Expired : StoredCredentialState.LooksValid;
    }

    public static string Render(StoredCredentialState state, StoredTokens? stored, string targetBaseUrl) =>
        state switch {
            StoredCredentialState.Missing => NotLoggedIn,

            StoredCredentialState.Expired =>
                "Your kcap login has expired and an automatic refresh did not succeed. " +
                "Run 'kcap login' on the host shell.",

            StoredCredentialState.WrongServer =>
                $"Stored login was issued by {stored?.ServerUrl} but this request targets {targetBaseUrl}. " +
                $"Run 'kcap login' (or switch profiles with 'kcap use') to authenticate against {targetBaseUrl}.",

            // The stored token is unexpired and bound to this server, was re-read from disk and
            // re-sent, and the server still said 401. Locally there is nothing left to fix —
            // this is the server's auth state having moved (a restart that rotated the signing
            // key, or an auth incident). Say so, and steer AWAY from the daemon-restart
            // superstition the incident produced.
            _ =>
                $"The server rejected kcap's credentials (HTTP 401) even after re-reading the token store — " +
                $"yet the stored login for {stored?.GitHubUsername} looks valid locally ({DescribeExpiry(stored)}). " +
                "This usually means the server's auth state changed (a restart or auth incident). " +
                "Run 'kcap login' on the host shell to mint a fresh credential — restarting the daemon will not help. " +
                "If a fresh login still hits this, the server is mid-incident; retry later.",
        };

    /// <summary>
    /// Store-inspecting convenience for the MCP servers' 401 sites. Raw, non-mutating read —
    /// deliberately never the refresh-aware accessor, which could rotate a credential just to
    /// build an error string. Any store fault degrades to the legacy message rather than
    /// replacing an auth diagnosis with an IO stack trace.
    /// </summary>
    public static async Task<string> ForPersistentUnauthorizedAsync(
            TokenStore store, string profile, string targetBaseUrl, CancellationToken ct = default) {
        try {
            var stored = await store.LoadForProfileAsync(profile, ct);

            return Render(Classify(stored, targetBaseUrl), stored, targetBaseUrl);
        } catch {
            return NotLoggedIn;
        }
    }

    static string DescribeExpiry(StoredTokens? stored) {
        if (stored is null) return "expiry unknown";

        var remaining = stored.ExpiresAt - DateTimeOffset.UtcNow;

        return remaining.TotalHours >= 1
            ? $"expires in {remaining.TotalHours:F0}h"
            : $"expires in {Math.Max(1, remaining.TotalMinutes):F0}m";
    }
}
