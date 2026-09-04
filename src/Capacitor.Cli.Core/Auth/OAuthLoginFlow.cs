using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Duende.IdentityModel.Client;
using Duende.IdentityModel.OidcClient;
using Duende.IdentityModel.OidcClient.Browser;

// ReSharper disable MethodHasAsyncOverload

using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Core.Auth;

public static class AuthProvider {
    public const string GitHubApp = "GitHubApp";
    public const string WorkOS    = "workos";
    public const string None      = "None";
}

public enum GitHubFlow { Browser, Device }

public enum WorkOSFlow { Browser, Device }

public static class OAuthLoginFlow {
    /// <summary>GET <c>{serverUrl}/auth/config</c>, or <c>null</c> with the failure already reported.</summary>
    internal static async Task<AuthDiscoveryResponse?> FetchAuthConfigAsync(
            HttpClient http, string serverUrl, CancellationToken ct, IAuthProgress progress) {
        HttpResponseMessage configResponse;

        try {
            configResponse = await http.GetAsync($"{serverUrl}/auth/config", ct);
        } catch (HttpRequestException ex) {
            progress.Error(HttpClientExtensions.UnreachableErrorText(serverUrl, ex));

            return null;
        }

        var config = configResponse.IsSuccessStatusCode
            ? await configResponse.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.AuthDiscoveryResponse, ct)
            : null;

        if (config is null) progress.Error($"Error: Failed to fetch auth config from {serverUrl}/auth/config");

        return config;
    }

    internal static GitHubFlow ChooseGitHubFlow(bool forceDevice, bool isHeadless, bool hasExchangeUrl)
        => forceDevice || isHeadless || !hasExchangeUrl ? GitHubFlow.Device : GitHubFlow.Browser;

    /// <summary>
    /// Only an explicit request skips the browser. There is deliberately no environment heuristic
    /// here: HeadlessEnvironment.IsHeadless() is true for every SSH session, including Remote-SSH and
    /// `ssh -L` where loopback works perfectly, so selecting on it would demote the largest developer
    /// population from "the browser opens" to "read a URL, type a code".
    /// </summary>
    internal static WorkOSFlow ChooseWorkOSFlow(bool forceDevice)
        => forceDevice ? WorkOSFlow.Device : WorkOSFlow.Browser;

    /// <summary>
    /// Whether a console can complete anything BUT the device grant. Redirected stdin cannot press the
    /// escape-hatch key, and a browser that launches without being able to reach us at 127.0.0.1 then
    /// costs the whole listener timeout and ends in nothing.
    /// <para>Console hosts only: a GUI has no keyboard either and rescues in its own UI, which is why
    /// this is not read off <see cref="IKeyWatcher.CanWatch"/> inside the ladder.</para>
    /// </summary>
    internal static bool DeviceRouteRequired(bool userAsked, bool consoleHasKeyboard)
        => userAsked || !consoleHasKeyboard;

    /// <summary>
    /// Picks the discovery provider before any auth runs: <c>--github</c> selects the GitHub App
    /// path, and everything else uses the org SSO path (WorkOS).
    ///
    /// <para><c>--device</c> deliberately does NOT route here: it means "use the device flow" on
    /// whichever provider discovery picked, and the org SSO path has one of its own. <c>--github</c> is
    /// the only route for a GitHub-App-only user, so it keeps its discovery meaning.</para>
    /// </summary>
    internal static string ChooseDiscoveryProvider(string[] args)
        => args.Contains("--github") ? AuthProvider.GitHubApp : AuthProvider.WorkOS;

    /// <summary>
    /// What a session with no keyboard is told once discovery has found no workspace. Signing in
    /// headless works; creating a workspace asks for an organization name and a slug, so without a
    /// terminal those two have to arrive as flags instead.
    /// </summary>
    internal static string WorkspaceCreationNeedsATerminalMessage() =>
        "Creating a workspace asks for a name and a slug, and this session is non-interactive.\n"
      + "  • Answer up front: kcap setup --org \"<name>\" --slug <slug> --no-prompt\n"
      + $"  • Or create one at {ProvisioningEndpoint.Url}/signup, then run: kcap setup <slug> --no-prompt\n"
      + "  • Or point at a workspace you already belong to: kcap setup --server-url <url> --no-prompt";

    /// <summary>
    /// `kcap login` runs tenant discovery when there's no configured server (nothing to log into yet)
    /// or the user explicitly asked with <c>--discover</c>; otherwise it logs into the configured server.
    /// </summary>
    internal static bool ShouldDiscoverLogin(string? baseUrl, string[] args)
        => args.Contains("--discover") || baseUrl is null;

    /// <summary>
    /// Runs GitHub Device Flow interactively. Reports the user code and verification URL, opens the
    /// browser on it, and polls GitHub for the access token, through <paramref name="progress"/>.
    /// Intended for CLI use — not suitable for headless callers.
    /// </summary>
    /// <returns>The GitHub access token on success, or <c>null</c> on failure.</returns>
    internal static async Task<string?> RunDeviceFlowAsync(
            GitHubOAuthClient github, string clientId, IBrowserLauncher launcher,
            CancellationToken ct = default, IAuthProgress? progress = null,
            TimeProvider? time = null) {
        progress ??= ConsoleAuthProgress.Instance;

        var deviceResponse = await github.RequestDeviceCodeAsync(clientId, ct);

        if (!deviceResponse.IsSuccessStatusCode) {
            progress.Error($"Error requesting device code: {await deviceResponse.Content.ReadAsStringAsync(ct)}");

            return null;
        }

        var device   = (await deviceResponse.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.DeviceCodeResponse, ct))!;
        var interval = device.IntervalOrDefault;

        var browserOpened = launcher.TryOpen(device.BrowserUri);
        var prefilled     = browserOpened && !string.IsNullOrEmpty(device.VerificationUriComplete);

        // Not copied when the page already carries the code: there is nothing to paste it into, and the
        // note is folded INTO the code below, so it would read as part of "Check the code shown is ...".
        var copied = !prefilled && Clipboard.TryCopy(device.UserCode);

        // The URL printed always matches the instruction under it. Opened: the complete one, which is
        // where the browser actually went, so following the line by hand lands on the same prefilled
        // page step 2 describes. Not opened: the bare one, because that URL is about to be retyped on
        // another device and a query string is the worst part of it to retype.
        var shownUri = browserOpened ? device.BrowserUri : device.VerificationUri;

        progress.Notice("");
        progress.Notice("To finish signing in to GitHub:");
        progress.Notice("");
        progress.Notice(
            browserOpened
                ? $"  1. Your browser should have opened {shownUri}"
                : $"  1. Open {shownUri} in a browser"
        );

        if (browserOpened) progress.Notice("     (if it didn't open, go to that URL yourself)");

        // Clipboard-copy suffix folds into the code: DeviceCode's contract carries no separate flag for it.
        progress.DeviceCode(device.UserCode + (copied ? "  (copied to clipboard)" : ""), shownUri, "GitHub", prefilled);

        return await PollDeviceGrantAsync(
            github.PollForTokenAsync,
            new() { ["client_id"] = clientId, ["device_code"] = device.DeviceCode },
            CapacitorJsonContext.Default.GitHubTokenResponse,
            r => (r.AccessToken, r.Error),
            device, interval, ct, progress, time);
    }

    /// <summary>
    /// RFC 8628 §3.4-3.5 polling, shared by every provider. Parameterised on the token URL, the extra
    /// form fields, and how to read a token and an error code out of the response.
    /// </summary>
    /// <param name="device">
    /// Carries the <c>expires_in</c> that bounds the loop. Without it the poll ran forever against a
    /// code the server had already discarded.
    /// </param>
    internal static async Task<T?> PollDeviceGrantAsync<T, TResponse>(
            Func<Dictionary<string, string>, CancellationToken, Task<HttpResponseMessage>> post,
            Dictionary<string, string> form,
            System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResponse> typeInfo,
            Func<TResponse, (T? Value, string? Error)> read,
            DeviceCodeResponse device, int interval,
            CancellationToken ct, IAuthProgress progress, TimeProvider? time = null,
            TimeSpan? attemptTimeout = null)
        where T : class where TResponse : class {
        time ??= TimeProvider.System;
        form["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code";

        var deadline = time.GetUtcNow().AddSeconds(device.ExpiresInOrDefault);

        while (true) {
            // The sleep stays on the real clock deliberately: `time` exists to bound the deadline, and
            // a fake one here would leave the timer waiting for an advance nobody makes.
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            ct.ThrowIfCancellationRequested();

            if (time.GetUtcNow() >= deadline) {
                progress.Error($"\nThe code expired before it was approved. Re-run to get a new one.");

                return null;
            }

            // Bound each attempt well inside the code's own lifetime: HttpClient's default timeout is
            // 100 seconds, which on a 300-second device code spends a third of the window on one hung
            // request.
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(attemptTimeout ?? TimeSpan.FromSeconds(20));

            HttpResponseMessage response;

            try {
                response = await post(form, attempt.Token);
            } catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested) {
                // A blip mid-poll is not a failed sign-in - the human is still at the browser, and the
                // deadline above is what ends this loop.
                interval += 5;
                progress.PollTick();

                continue;
            }

            // Disposed at the end of every iteration, whichever way this one leaves: a long poll
            // otherwise piles up one undisposed response per tick for the code's whole lifetime.
            using var polled = response;

            // Parse the body whatever the status: RFC 8628 §3.5 carries authorization_pending in a 400,
            // and GitHub returns it in a 200. Only an UNREADABLE body is a transport problem - a 429 or
            // a 5xx HTML page - and it is retried rather than force-unwrapped.
            TResponse? parsed = null;

            try {
                parsed = await response.Content.ReadFromJsonAsync(typeInfo, attempt.Token);
            } catch (Exception ex) when (ex is System.Text.Json.JsonException or HttpRequestException
                                          || (ex is OperationCanceledException && !ct.IsCancellationRequested)) {
                // fall through to the transient arm below
            }

            if (parsed is null) {
                if (response.IsSuccessStatusCode) {
                    progress.Error("\nThe sign-in service returned an unreadable response.");

                    return null;
                }

                // Transient by elimination: a non-2xx we could not parse. Back off and keep polling
                // until the deadline rather than failing a sign-in the user may still be completing.
                interval += 5;
                progress.PollTick();

                continue;
            }

            var (value, error) = read(parsed);

            if (value is not null) {
                progress.Notice(" done!");

                return value;
            }

            switch (error) {
                case "authorization_pending":
                    progress.PollTick();

                    continue;
                case "slow_down":
                    interval += 5;

                    continue;
                case "expired_token":
                    progress.Error("\nThe code expired before it was approved. Re-run to get a new one.");

                    return null;
                case "access_denied":
                    progress.Error("\nThe request was denied.");

                    return null;
                default:
                    // A non-2xx carrying no RFC 8628 error code is the service having a moment, not a
                    // decision about this sign-in: a 5xx `{"message":"..."}` parses cleanly into a
                    // response with every field defaulted, so only the status tells the two apart.
                    // On a 2xx an unrecognised code IS the answer, and stays fatal.
                    if (!response.IsSuccessStatusCode) {
                        interval += 5;
                        progress.PollTick();

                        continue;
                    }

                    progress.Error($"\nError: {error ?? "unknown error"}");

                    return null;
            }
        }
    }

    /// <summary>
    /// GitHub authorization-code-with-PKCE via OidcClient's front-channel (authorize URL + PKCE +
    /// state) over a 127.0.0.1 loopback, then the proxy-mediated JSON code-exchange to the Capacitor
    /// server (GitHub Apps need client_secret on the token POST, which the server adds). Returns the
    /// GitHub access token, or <c>null</c> on cancel/timeout/state-mismatch/error — a null is a hard
    /// failure (the caller does NOT fall back to device flow on null, only on a loopback bind exception
    /// thrown out of <see cref="LoopbackBrowser"/>). <paramref name="browser"/> is the test seam.
    /// </summary>
    public static async Task<string?> RunGitHubBrowserFlowAsync(
            GitHubOAuthClient github, string clientId, string codeExchangeUrl, IBrowserLauncher launcher,
            IBrowser? browser = null, TimeSpan? timeout = null,
            CancellationToken ct = default, IAuthProgress? progress = null) {
        progress ??= ConsoleAuthProgress.Instance;

        // Owned-vs-borrowed, in one line: `created` is disposed, the injected `browser` never is. A
        // locally-built LoopbackBrowser owns a listener that outlives InvokeAsync (the return-hop
        // wait), so leaving it inline would hold the port for the life of the process; disposing an
        // injected one would tear down a test's stand-in, or a future caller's shared instance.
        // `using` on a nullable disposes only when non-null, which is exactly the distinction.
        using LoopbackBrowser? created =
            browser is null ? new LoopbackBrowser(launcher, progress, join: SetupJoin.Loopback) : null;
        browser ??= created!; // non-null exactly when browser was null, which is when we built it

        var redirectUri = $"http://127.0.0.1:{GetAvailablePort()}/callback";

        var options = new OidcClientOptions {
            Authority   = "https://github.com",
            ClientId    = clientId,
            Scope       = "read:user read:org",
            RedirectUri = redirectUri,
            LoadProfile = false,
            DisablePushedAuthorization = true,
            Browser     = browser,
            ProviderInformation = new ProviderInformation {
                IssuerName        = "https://github.com",
                AuthorizeEndpoint = "https://github.com/login/oauth/authorize",
                TokenEndpoint     = "https://github.com/login/oauth/access_token", // required non-empty; never called
            },
        };
        options.Policy.Discovery.RequireKeySet = false;

        var oidc  = new OidcClient(options);
        var state = await oidc.PrepareLoginAsync(cancellationToken: ct);

        var result = await browser.InvokeAsync(
            new BrowserOptions(state.StartUrl, redirectUri) { Timeout = timeout ?? TimeSpan.FromMinutes(5) }, ct);

        if (result.ResultType != BrowserResultType.Success) {
            ct.ThrowIfCancellationRequested(); // a caller cancel is neither a timeout nor a denial
            progress.Error(result.ResultType == BrowserResultType.Timeout
                ? "Timed out waiting for authorization. Re-run `kcap login` to try again."
                : $"Authorization failed: {result.Error ?? result.ResultType.ToString()}");

            return null;
        }

        var resp = new AuthorizeResponse(result.Response);
        if (resp.IsError) {
            progress.Error($"Authorization failed: {resp.Error}");

            return null;
        }
        if (!string.Equals(resp.State, state.State, StringComparison.Ordinal)) {
            progress.Error("Error: state mismatch — possible CSRF. Aborting.");

            return null;
        }
        if (string.IsNullOrEmpty(resp.Code)) {
            progress.Error("Authorization failed: no authorization code received.");

            return null;
        }

        // Bound the proxy exchange to the login timeout — a stalled endpoint must not hang the CLI —
        // while still observing the caller's own cancellation.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));

        HttpResponseMessage tokenResponse;

        try {
            tokenResponse = await github.ExchangeCodeAsync(
                codeExchangeUrl,
                new GitHubCodeExchangeRequest { Code = resp.Code, CodeVerifier = state.CodeVerifier, RedirectUri = redirectUri },
                cts.Token
            );
        } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException) {
            ct.ThrowIfCancellationRequested(); // the caller's cancel aborted the exchange — not a connectivity failure
            progress.Error($"Could not reach the code-exchange endpoint at {codeExchangeUrl}: {ex.Message}");

            return null;
        }

        if (!tokenResponse.IsSuccessStatusCode) {
            progress.Error($"Error exchanging code: {await tokenResponse.Content.ReadAsStringAsync(cts.Token)}");

            return null;
        }

        GitHubTokenResponse? tokenResult;

        try {
            tokenResult = await tokenResponse.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.GitHubTokenResponse, cancellationToken: cts.Token);
        } catch (JsonException ex) {
            var raw = await tokenResponse.Content.ReadAsStringAsync(cts.Token);
            progress.Error($"Code-exchange response was not valid JSON ({ex.Message}): {raw}");

            return null;
        }

        if (tokenResult?.AccessToken is null) {
            progress.Error($"Error: {tokenResult?.Error ?? "no access_token in response"}");

            return null;
        }

        progress.Notice("Authorization complete.");

        return tokenResult.AccessToken;
    }

    /// <summary>
    /// Exchanges a GitHub access token for a Capacitor JWT and returns the tokens WITHOUT saving —
    /// persistence belongs to the caller's commit boundary. <c>null</c> means the exchange failed and
    /// the reason has already been reported through <paramref name="progress"/>. <paramref name="profile"/>
    /// only names the profile in that error text.
    /// </summary>
    public static async Task<(StoredTokens Tokens, string? Username)?> ExchangeAsync(
            HttpClient        http,
            string            serverUrl,
            string            githubAccessToken,
            string            provider,
            string?           profile,
            IAuthProgress     progress,
            CancellationToken ct = default) {
        if (provider is not AuthProvider.GitHubApp) {
            progress.Error($"Error: unknown auth provider '{provider}'");

            return null;
        }

        var exchangeResponse = await http.PostAsJsonAsync(
            $"{serverUrl}/auth/token",
            new TokenExchangeRequest { GithubAccessToken = githubAccessToken },
            CapacitorJsonContext.Default.TokenExchangeRequest,
            cancellationToken: ct
        );

        if (!exchangeResponse.IsSuccessStatusCode) {
            WriteExchangeError(await exchangeResponse.Content.ReadAsStringAsync(ct), profile, progress);

            return null;
        }

        var exchange = (await exchangeResponse.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.TokenExchangeResponse, ct))!;

        if (!ServerIdentity.TryCanonicalizeForStamping(serverUrl, out var canonical, out var identityError)) {
            progress.Error($"Error: {identityError}");

            return null;
        }

        return (new StoredTokens {
            AccessToken    = exchange.AccessToken,
            ExpiresAt      = DateTimeOffset.UtcNow.AddSeconds(exchange.ExpiresIn),
            GitHubUsername = exchange.Username,
            Provider       = provider,
            ServerUrl      = canonical
        }, exchange.Username);
    }

    /// <summary>
    /// Reports the server's <c>/auth/token</c> error. When the server reports that the Capacitor
    /// GitHub App isn't installed on the user's org, appends a troubleshooting checklist — the most
    /// common cause is the device-flow consent being completed under a different GitHub account than
    /// the one with org membership.
    /// </summary>
    static void WriteExchangeError(string body, string? profile, IAuthProgress progress) {
        var prefix = profile is null
            ? "Error exchanging token"
            : $"Error exchanging token for profile '{profile}'";

        var serverMessage = TryParseInstallationMessage(body);

        if (serverMessage is null) {
            progress.Error($"{prefix}: {body}");

            return;
        }

        progress.Error($"{prefix}: {serverMessage}");
        progress.Error("");
        progress.Error("This usually means the Capacitor GitHub App isn't visible to your GitHub user.");
        progress.Error("Common fixes:");
        progress.Error("  1. Authorize as the right GitHub account. The device-flow page authorizes");
        progress.Error("     whoever is signed in to your browser — sign in to https://github.com as");
        progress.Error("     your org user, then re-run `kcap setup ...`.");
        progress.Error("  2. If your org enforces SAML SSO, authorize SSO for the App at");
        progress.Error("     https://github.com/settings/apps/authorizations.");
        progress.Error("  3. Revoke a stale prior authorization at the same URL and retry.");
        progress.Error("");
        progress.Error("If the App was never installed on your org, an org admin must install it.");
    }

    internal static string? TryParseInstallationMessage(string body) {
        if (string.IsNullOrWhiteSpace(body)) return null;

        try {
            var parsed = JsonSerializer.Deserialize(body, CapacitorJsonContext.Default.AuthErrorResponse);
            var msg    = parsed?.Error;

            return msg is not null && msg.Contains("not installed", StringComparison.OrdinalIgnoreCase)
                ? msg
                : null;
        } catch (JsonException) {
            return null;
        }
    }

    internal static async Task<string?> AcquireGitHubTokenAsync(
            GitHubOAuthClient github, string clientId, string? codeExchangeUrl, bool forceDevice,
            IBrowserLauncher launcher,
            CancellationToken ct = default, IAuthProgress? progress = null) {
        progress ??= ConsoleAuthProgress.Instance;

        var headless = HeadlessEnvironment.IsHeadless();
        var choice   = ChooseGitHubFlow(forceDevice, headless, hasExchangeUrl: IsValidExchangeUrl(codeExchangeUrl));

        if (choice == GitHubFlow.Browser) {
            try {
                var token = await RunGitHubBrowserFlowAsync(
                    github, clientId, codeExchangeUrl!, launcher, ct: ct, progress: progress);

                return token ??
                    // Browser flow ran but user cancelled / state mismatch — don't silently fall back.
                    null;
            } catch (BrowserLaunchException) {
                progress.Notice("No browser on this machine, so switching to a device code you can use anywhere.");
            } catch (HttpListenerException ex) {
                progress.Error($"Could not bind loopback listener ({ex.Message}); falling back to device flow.");
            } catch (PlatformNotSupportedException ex) {
                progress.Error($"Loopback listener not supported on this platform ({ex.Message}); falling back to device flow.");
            }
        }

        return await RunDeviceFlowAsync(github, clientId, launcher, ct, progress);
    }

    internal const string WorkOSApiBase = "https://api.workos.com";

    /// <summary>
    /// Builds OidcClient options for the WorkOS AuthKit authorization-code-with-PKCE flow.
    /// Authorize + token both on the API domain (never the AuthKit UI domain). WorkOS is a
    /// public client (no secret) with non-standard endpoints, no discovery, and no id_token, so
    /// discovery/keyset/userinfo are disabled and the response is mapped by hand.
    /// </summary>
    internal static OidcClientOptions BuildWorkOSOptions(string clientId, string apiBase, string redirectUri) {
        var options = new OidcClientOptions {
            Authority   = apiBase,            // anonymous-principal issuer; discovery stays off (ProviderInformation set)
            ClientId    = clientId,
            Scope       = "",                 // preserve current no-scope behavior
            RedirectUri = redirectUri,
            LoadProfile = false,              // WorkOS has no userinfo endpoint
            DisablePushedAuthorization = true,
            ProviderInformation = new ProviderInformation {
                IssuerName        = apiBase,
                AuthorizeEndpoint = $"{apiBase}/user_management/authorize",     // always the API domain
                TokenEndpoint     = $"{apiBase}/user_management/authenticate",
            },
        };
        options.Policy.Discovery.RequireKeySet = false;

        return options;
    }

    /// <summary>WorkOS front-channel extras: <c>provider=authkit</c> (+ <c>organization_id</c> when org-scoped).</summary>
    internal static Parameters WorkOSFrontChannel(string? organizationId) {
        var p = new Parameters { { "provider", "authkit" } };
        if (!string.IsNullOrEmpty(organizationId)) p.Add("organization_id", organizationId);

        return p;
    }

    /// <summary>
    /// WorkOS AuthKit authorization-code-with-PKCE login via OidcClient. Org-scoped when
    /// <paramref name="organizationId"/> is set. Maps the raw token response (which carries WorkOS's
    /// non-standard organization_id/user and no id_token) into <see cref="WorkOSAuthResponse"/> via the
    /// source-gen context — omitted/nullable fields don't throw. <paramref name="apiBase"/> is the test seam.
    /// </summary>
    public static async Task<WorkOSAuthResponse?> AuthenticateWorkOSAsync(
            string clientId, string? organizationId, IBrowser browser, string apiBase = WorkOSApiBase,
            CancellationToken ct = default, IAuthProgress? progress = null) {
        progress ??= ConsoleAuthProgress.Instance;

        var redirectUri = $"http://127.0.0.1:{GetAvailablePort()}/callback";
        var options     = BuildWorkOSOptions(clientId, apiBase, redirectUri);
        options.Browser = browser;

        var oidc   = new OidcClient(options);
        var result = await oidc.LoginAsync(new LoginRequest { FrontChannelExtraParameters = WorkOSFrontChannel(organizationId) }, ct);

        // Surface the actual reason (timeout / state mismatch / token-endpoint / upstream OIDC error)
        // rather than collapsing every failure to a single opaque "sign-in failed".
        if (result.IsError) {
            ct.ThrowIfCancellationRequested(); // OidcClient renders a caller cancel as an error result
            progress.Error(WorkOSSignInError(result.Error, result.ErrorDescription));

            return null;
        }

        if (result.TokenResponse?.Json is not { } json) {
            progress.Error("WorkOS sign-in failed: empty token response.");

            return null;
        }

        return JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.WorkOSAuthResponse);
    }

    /// <summary>
    /// RFC 8628 against AuthKit (<c>api.workos.com/user_management/*</c>), NOT Connect's
    /// <c>{authkit-domain}/oauth2/*</c> — that one returns no organization and requires a client secret.
    /// Public client: no secret anywhere in this flow, so the proxy is not involved.
    /// </summary>
    internal static async Task<WorkOSAuthResponse?> RunWorkOSDeviceFlowAsync(
            WorkOSClient workos, string clientId, IBrowserLauncher launcher,
            CancellationToken ct = default, IAuthProgress? progress = null, TimeProvider? time = null) {
        progress ??= ConsoleAuthProgress.Instance;

        var authorize = await workos.AuthorizeDeviceAsync(clientId, ct);

        if (!authorize.IsSuccessStatusCode) {
            // Step 1 failing is the rollout hazard, not a user error: if CLI Auth is not enabled on this
            // AuthKit client every headless sign-in dies here, and a generic message would send people
            // hunting through the rest of setup.
            progress.Error("Could not start device sign-in.");
            progress.Error($"  The sign-in service answered {(int)authorize.StatusCode}.");
            progress.Error("  If this persists, device sign-in may not be enabled for this workspace,");
            progress.Error("  and an administrator has to turn it on.");

            return null;
        }

        DeviceCodeResponse? device = null;

        try {
            device = await authorize.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.DeviceCodeResponse, ct);
        } catch (System.Text.Json.JsonException) {
            // fall through
        }

        if (device is null || string.IsNullOrEmpty(device.DeviceCode)) {
            progress.Error("The sign-in service returned no device code.");

            return null;
        }

        // Best-effort: the population this flow exists for has no browser here at all.
        var browserOpened = launcher.TryOpen(device.BrowserUri);

        // The URL printed always matches the instruction under it. Opened: the complete one, which is
        // where the browser actually went, so following the line by hand lands on the same prefilled
        // page step 2 describes. Not opened: the bare one, because that URL is about to be retyped on
        // another device and a query string is the worst part of it to retype.
        var shownUri = browserOpened ? device.BrowserUri : device.VerificationUri;

        progress.Notice("");
        progress.Notice("To finish signing in:");
        progress.Notice("");
        progress.Notice(
            browserOpened
                ? $"  1. Your browser should have opened {shownUri}"
                : $"  1. Open {shownUri} in a browser");

        // No clipboard copy, unlike the GitHub flow: the code has to be READ ALOUD OR RETYPED on
        // another device for this flow to mean anything, and a silent copy invites pasting it into
        // whatever page is already open.
        progress.DeviceCode(device.UserCode, shownUri, provider: null,
            prefilled: browserOpened && !string.IsNullOrEmpty(device.VerificationUriComplete));

        var token = await PollDeviceGrantAsync(
            workos.PollForTokenAsync,
            new() { ["client_id"] = clientId, ["device_code"] = device.DeviceCode },
            CapacitorJsonContext.Default.WorkOSAuthResponse,
            r => (string.IsNullOrEmpty(r.AccessToken) ? null : r, r.Error),
            device, device.IntervalOrDefault, ct, progress, time);

        // Stamped here rather than at the ladder's fall-through points, because this is the one
        // place every device-code answer passes through.
        return token is null ? null : token with { ViaDeviceGrant = true };
    }

    internal const char EscapeHatchKey = 'd';

    /// <summary>
    /// The WorkOS sign-in ladder, and the single entry point for both call sites: loopback, with the
    /// device grant reachable by pressing <c>d</c> at any point and taken automatically when loopback
    /// cannot bind. A loopback attempt that RAN and failed returns <c>null</c> rather than falling
    /// through — a cancel or a state mismatch is an answer, and silently re-asking through another
    /// channel would ignore it. Mirrors <see cref="AcquireGitHubTokenAsync(GitHubOAuthClient,string,string?,bool,IBrowserLauncher,CancellationToken,IAuthProgress?)"/>.
    /// </summary>
    internal static async Task<WorkOSAuthResponse?> AcquireWorkOSAsync(
            WorkOSClient workos, string clientId, string? organizationId, bool forceDevice,
            IBrowserLauncher launcher,
            IBrowser? browser = null, string apiBase = WorkOSApiBase, CancellationToken ct = default,
            IAuthProgress? progress = null, IKeyWatcher? keys = null, TimeProvider? time = null) {
        progress ??= ConsoleAuthProgress.Instance;
        keys     ??= ConsoleKeyWatcher.Instance;

        if (ChooseWorkOSFlow(forceDevice) is WorkOSFlow.Device) {
            return await RunWorkOSDeviceFlowAsync(workos, clientId, launcher, ct, progress, time);
        }

        using var escape = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var settled = new CancellationTokenSource();

        // Owned, not an inline sub-expression: with a join attached the listener OUTLIVES
        // AuthenticateWorkOSAsync so it can catch the browser's return hop, so an unnamed instance
        // would hold the port for the life of the process — for the desktop app, the whole session.
        // `using` on a nullable disposes exactly when the local is the one we built, never the
        // injected test seam.
        //
        // Scoped to the whole ladder, not to the login task: the fall-through paths below still run
        // with it alive. That gives the drain only Dispose's own bounded wait once login succeeds,
        // rather than the longer tail the facade used to provide — enough in practice (the production
        // round trip merged in under a second) but genuinely tighter, not equivalent.
        using LoopbackBrowser? created = browser is null
            ? new LoopbackBrowser(
                launcher, progress, keys.CanWatch ? WorkOSBrowserHint() : null, SetupJoin.Loopback)
            : null;

        var login = AuthenticateWorkOSAsync(
            clientId, organizationId,
            // No keyboard, no hint: a GUI host cannot act on one, and a console without a keyboard
            // never reaches here (DeviceRouteRequired sent it to the device grant already).
            browser ?? created!,
            apiBase, escape.Token, progress);
        var watch = WatchForEscapeHatchAsync(keys, escape, settled.Token);

        try {
            return await login;
        } catch (OperationCanceledException) when (escape.IsCancellationRequested && !ct.IsCancellationRequested) {
            // Only the watcher cancels `escape` without `ct`, so this is the escape hatch and nothing else.
            progress.Notice("Switching to a device code.");
        } catch (BrowserLaunchException) {
            // Not an error, and deliberately says nothing about the loopback URL: there is no browser
            // here, so the user's is on another machine and 127.0.0.1 is not us. A device code is the
            // route that works from any machine, which is what they actually need.
            progress.Notice("No browser on this machine, so switching to a device code you can use anywhere.");
        } catch (HttpListenerException ex) {
            progress.Error($"Could not bind loopback listener ({ex.Message}); using a device code instead.");
        } catch (PlatformNotSupportedException ex) {
            progress.Error($"Loopback listener not supported on this platform ({ex.Message}); using a device code instead.");
        } finally {
            await settled.CancelAsync();
            await watch;
        }

        return await RunWorkOSDeviceFlowAsync(workos, clientId, launcher, ct, progress, time);
    }

    /// <summary>
    /// The escape hatch, worded to sit directly under the "visit:" URL as an alternative to it. Offered
    /// only where there is a keyboard to take it with - see the caller for why there is no wording for
    /// the other case rather than a different one.
    /// </summary>
    internal static string WorkOSBrowserHint() => $"  Or press {EscapeHatchKey} to switch to a device code.";

    /// <summary>
    /// Polls for the escape-hatch key while the browser leg is in flight, cancelling
    /// <paramref name="escape"/> when it arrives. Polls rather than blocks: a blocking read would
    /// outlive a browser sign-in that succeeded and hold the CLI on a keypress nobody owes it.
    /// </summary>
    /// <param name="settled">Cancelled by the caller once the browser leg is done, either way.</param>
    internal static async Task WatchForEscapeHatchAsync(
            IKeyWatcher keys, CancellationTokenSource escape, CancellationToken settled) {
        if (!keys.CanWatch) return;

        try {
            while (true) {
                await Task.Delay(TimeSpan.FromMilliseconds(120), settled);

                // Re-read `settled` before touching the keyboard: once the browser leg has finished,
                // anything buffered belongs to the next prompt rather than to this one.
                if (settled.IsCancellationRequested) return;

                if (!keys.KeyAvailable || char.ToLowerInvariant(keys.ReadKey()) != EscapeHatchKey) continue;

                keys.Drain();
                await escape.CancelAsync();

                return;
            }
        } catch (OperationCanceledException) {
            // The browser leg settled first — nothing to hand over.
        }
    }

    /// <summary>Maps an OidcClient WorkOS failure to a user-facing message, preserving the actionable detail.</summary>
    internal static string WorkOSSignInError(string? error, string? description) => error switch {
        "Timeout"    => "Timed out waiting for authorization. Re-run `kcap login` to try again.",
        "UserCancel" => "Sign-in was cancelled.",
        _            => $"Sign-in failed: {error ?? "unknown error"}"
                      + (string.IsNullOrEmpty(description) ? "" : $" - {description}")
    };

    /// <summary>
    /// WorkOS sign-in against a KNOWN server: authenticate, enforce the org gate, and build the
    /// server-bound tokens WITHOUT saving them. <c>null</c> means the reason is already reported.
    /// </summary>
    internal static async Task<(StoredTokens Tokens, string Username)?> WorkOSTokensForServerAsync(
            WorkOSClient workos, string serverUrl, string clientId, string? organizationId, bool forceDevice,
            IBrowserLauncher launcher,
            IBrowser? browser, CancellationToken ct, IAuthProgress progress, string apiBase = WorkOSApiBase,
            IKeyWatcher? keys = null, TimeProvider? time = null) {
        // AcquireWorkOSAsync already reported the specific failure reason.
        var json = await AcquireWorkOSAsync(
            workos, clientId, organizationId, forceDevice, launcher, browser, apiBase, ct, progress, keys, time);
        if (json is null) return null;

        // Org gate: a multi-org user must not be "logged in" to the wrong org — every API call would
        // then fail the server's org check.
        var username = WorkOSDisplayName(json.User);

        if (!string.IsNullOrEmpty(organizationId) && !string.Equals(json.OrganizationId, organizationId, StringComparison.Ordinal)) {
            // Correct rather than reject. The device grant's authorize leg takes no organization_id, so
            // the human picks at the AuthKit screen and the CLI cannot constrain it — telling them to
            // re-run would send them back to the same unconstrained screen. The switch is gated on
            // their own membership, so a user with no claim to the org still lands on the error below.
            json = await CorrectWorkOSOrgAsync(workos, clientId, json, organizationId, ct);

            if (json is null) {
                progress.Error("Error: Signed in to the wrong workspace. Sign in again and choose the one this server belongs to.");

                return null;
            }
        }

        if (!ServerIdentity.TryCanonicalizeForStamping(serverUrl, out var canonical, out var identityError)) {
            progress.Error($"Error: {identityError}");

            return null;
        }

        return (new StoredTokens {
            AccessToken    = json.AccessToken,
            RefreshToken   = json.RefreshToken,
            ExpiresAt      = TokenStore.JwtExpiry(json.AccessToken),
            GitHubUsername = username,
            Provider       = AuthProvider.WorkOS,
            ClientId       = clientId,
            // The kcap server we authenticated FOR — not api.workos.com, which issued the
            // token but says nothing about which Capacitor server will accept it.
            ServerUrl      = canonical
        }, username);
    }

    /// <summary>Human display name from a WorkOS user (first+last, else email, else "unknown").</summary>
    internal static string WorkOSDisplayName(WorkOSUserInfo? user) {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(user?.FirstName)) parts.Add(user!.FirstName!);
        if (!string.IsNullOrEmpty(user?.LastName))  parts.Add(user!.LastName!);

        return parts.Count > 0 ? string.Join(' ', parts) : user?.Email ?? "unknown";
    }

    /// <summary>
    /// Moves an authenticated session onto <paramref name="organizationId"/>, or <c>null</c> when it
    /// cannot be moved there. Verifies the org on the way back out: a switch that answers 200 with a
    /// different organization has not corrected anything, and accepting it would store a token the
    /// server rejects on every call.
    /// </summary>
    internal static async Task<WorkOSAuthResponse?> CorrectWorkOSOrgAsync(
            WorkOSClient workos, string clientId, WorkOSAuthResponse signedIn,
            string organizationId, CancellationToken ct) {
        if (string.IsNullOrEmpty(signedIn.RefreshToken)) return null;

        var switched = await workos.SwitchOrganizationAsync(clientId, signedIn.RefreshToken, organizationId, ct);

        if (switched is null
         || string.IsNullOrEmpty(switched.AccessToken)
         || !string.Equals(switched.OrganizationId, organizationId, StringComparison.Ordinal)) {
            return null;
        }

        // The refresh_token grant answers without a user, so carry the sign-in's own across or the
        // display name collapses to "unknown".
        return switched.User is null ? switched with { User = signedIn.User } : switched;
    }

    // The server-supplied code-exchange URL must be a fully-qualified http(s) URI before
    // we trust it. An empty string, whitespace, relative path, or javascript:/file: URL
    // is treated as "no browser flow available" and the dispatcher falls back to device flow.
    internal static bool IsValidExchangeUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
     && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);

    internal static int GetAvailablePort() {
        var tcpListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start();
        var port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
        tcpListener.Stop();

        return port;
    }
}
