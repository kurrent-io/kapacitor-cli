using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.SessionStartMemory;

/// <summary>
/// The SessionStart memory wiring that is identical across every vendor adapter. The hooks own their
/// envelope, eligibility and ordering — those genuinely differ per harness — so only what does not
/// vary lives here.
/// </summary>
internal static class SessionStartMemoryHookSupport {
    /// <summary>
    /// Builds the combined memory + guidelines SessionStart context provider. Both lanes draw from one
    /// authenticated client accessor and the composite resolves the repo/machine scope ONCE for both.
    /// Which lanes actually run is decided per request via
    /// <see cref="SessionStartMemoryContextRequest.Disabled"/> (memory) and its
    /// <c>GuidelinesDisabled</c> flag — a disabled lane contributes nothing.
    ///
    /// <para>This is the single construction site for the eight non-Claude harnesses. Claude does NOT
    /// use it — it keeps a memory-only <see cref="SessionStartMemoryContextProvider"/> and renders
    /// guidelines from its own hook POST response.</para>
    ///
    /// <para><paramref name="client"/> is invoked inside the fetch, not here: the lifecycle gate runs
    /// first and declines most invocations, and resolving a credential ahead of it charges every one
    /// of those for auth work the decision then discards. It must carry a bearer —
    /// <c>/api/memories/index</c> is authenticated, so an anonymous client leaves the harness silently
    /// without memory context on any authenticated deployment.</para>
    /// </summary>
    public static ISessionStartContextProvider CompositeProvider(
            ConfigRoot config,
            Func<CancellationToken, Task<HttpClient>> client,
            ISessionStartMemoryScopeResolver? scopeResolver = null) {
        var resolver = scopeResolver ?? new SessionStartMemoryScopeResolver(config);

        var memory     = new SessionStartMemoryContextProvider(resolver, client);
        var guidelines = new SessionStartGuidelinesLane(client);
        return new SessionStartCompositeContextProvider(resolver, memory, guidelines);
    }

    /// <summary>
    /// Awaits an in-flight fragment fetch under the budget remaining AT THIS INSTANT — never the
    /// budget it was started with. On expiry the fetch is abandoned rather than cancelled mid-flight
    /// (its own lease bookkeeping owns that) and null is returned, so the caller's output degrades to
    /// "no memory" instead of being delayed past the harness's hook ceiling. Never throws.
    ///
    /// <para><see cref="HookBudget.Remaining"/> ALREADY reserves <see cref="HookBudget.Safety"/> for
    /// serialization and the write itself — do not subtract it again here or at the call site. Doing
    /// so cut the usable window from 3.5s to 2s at a fresh hook start and silently discarded healthy
    /// 2–3.5s responses that fit the intended ceiling.</para>
    /// </summary>
    public static async Task<string?> AwaitBounded(Task<string?> task, HookBudget budget) {
        try {
            var remaining = budget.Remaining;

            if (remaining <= TimeSpan.Zero)
                return task.IsCompletedSuccessfully ? task.Result : null;

            return await task.WaitAsync(remaining);
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            return null;
        }
    }

    /// <summary>
    /// Maps a harness-reported SessionStart source to the shared lifecycle reason. Unknown values map
    /// to <see cref="SessionLifecycleReason.Unknown"/> rather than being guessed as New — the lifecycle
    /// policy decides eligibility from this, so inventing a reason would invent an injection decision.
    /// </summary>
    public static SessionLifecycleReason ReasonFor(string? source) => source?.ToLowerInvariant() switch {
        "startup" or "new" => SessionLifecycleReason.New,
        "resume"           => SessionLifecycleReason.Resume,
        "reopen"           => SessionLifecycleReason.Reopen,
        "fork"             => SessionLifecycleReason.Fork,
        "compact"          => SessionLifecycleReason.Compact,
        null or ""         => SessionLifecycleReason.New,
        _                  => SessionLifecycleReason.Unknown
    };
}
