namespace Capacitor.Cli.Core.FirstRun;

/// <summary>
/// Says this machine is still here, on its own timer, for as long as a flow is being waited on.
///
/// <para><b>Deliberately not driven by the poll.</b> The poll stops for the whole of an import — the
/// loop blocks on it and adds the elapsed time back to its own deadline — so liveness derived from the
/// poll would declare the machine gone during the one stretch it is working hardest. A separate timer
/// measures the process, which is the only thing a beat can honestly claim.</para>
///
/// <para><b>Liveness of the process, never of the work.</b> A wedged leg goes on beating. What this
/// catches is the deaths that send nothing at all — SIGKILL, power loss, a shut lid, a dropped
/// network — which is exactly the class a relinquish notice structurally cannot reach.</para>
/// </summary>
public sealed class FirstRunHeartbeat : IDisposable {
    /// <summary>Comfortably inside the server's staleness window, so a single dropped beat is not a
    /// verdict. Lighter than the 2s poll it runs beside.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How many beats may be outstanding at once.
    ///
    /// <para>More than one, or a single slow request becomes the whole liveness budget: the loop would
    /// issue nothing until the answer came back, so a request that outlives its interval halves the
    /// cadence and one that hangs to the client's timeout silences the machine for the whole of it.
    /// Bounded all the same, because a wedged network would otherwise accumulate one open POST per
    /// interval on the very machine whose network is failing.</para>
    /// </summary>
    const int MaxInFlight = 3;

    /// <summary>Consecutive not-found answers before the beat backs off the route. More than one, so a
    /// blip cannot silence it; small, because a route that is genuinely absent answers this way every
    /// time and beating on is hundreds of authenticated no-ops per run.</summary>
    const int UnavailableBeforeBackingOff = 3;

    /// <summary>
    /// How long to go quiet after the route has refused often enough to look absent.
    ///
    /// <para>A pause rather than an ending. A rolling deploy or a proxy reload is minutes long and the
    /// 2s poll on the same client rides straight through it, so a beat that stopped for good would have
    /// the browser infer the machine has gone from one that is demonstrably still talking to it.</para>
    /// </summary>
    static readonly TimeSpan UnavailableBackoff = TimeSpan.FromMinutes(2);

    /// <summary>How long to go quiet on a throttle that names no delay.</summary>
    static readonly TimeSpan ThrottleBackoff = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The longest a throttle may silence the machine, however long the server asks for.
    ///
    /// <para>The heartbeat has its own limiter, so the poll can be succeeding every 2s while this route
    /// is refused — and an unclamped <c>Retry-After: 3600</c> would tell the browser the machine had gone
    /// for longer than the whole leg, with a working connection either side of it.</para>
    /// </summary>
    static readonly TimeSpan MaxThrottleBackoff = TimeSpan.FromMinutes(2);

    readonly CancellationTokenSource _stopping = new();
    readonly Task                    _beating;

    int _stopped;

    FirstRunHeartbeat(
            IFirstRunFlowChannel channel, string serverUrl, string flowId, TimeProvider clock,
            TimeSpan interval) =>
        _beating = BeatAsync(channel, serverUrl, flowId, clock, interval, _stopping);

    /// <summary>Starts beating immediately, so a flow becomes observably live without waiting out a
    /// first interval. Dispose to stop.</summary>
    public static FirstRunHeartbeat Start(
            IFirstRunFlowChannel channel, string serverUrl, string flowId, TimeProvider clock,
            TimeSpan? interval = null) =>
        new(channel, serverUrl, flowId, clock, interval ?? Interval);

    /// <summary>
    /// Stops scheduling. Nothing is cancelled: the request a beat is sitting in may be recovering a
    /// credential, and killing it there is what strands a rotated refresh token — see
    /// <see cref="SendAsync"/>. An outstanding beat is left to finish or to time out on the client's
    /// own deadline, and a beat that lands late costs nothing a stated ending does not outrank.
    /// </summary>
    public void Dispose() {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;

        _stopping.Cancel();

        // Observed so a fault cannot surface as an unobserved task exception. Every beat is wrapped
        // before it is awaited, so there is only ever the cancel we just asked for.
        _ = _beating.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);

        // After the cancel and the observation, and tolerated by the loop: a tick entering
        // WaitForNextTickAsync as this runs sees ObjectDisposedException, which ends it exactly as the
        // cancel would. Left undisposed the source leaks per leg, and stops being harmless the moment
        // anything here links one or sets a deadline on it.
        _stopping.Dispose();
    }

    /// <summary>
    /// Beats on the tick, harvesting answers as they arrive.
    ///
    /// <para><b>Answers are read, never dropped.</b> A throttling or absent-route server is exactly the
    /// one whose reply outruns an interval, so abandoning a slow request would miss the two statuses
    /// worth reading precisely when they matter.</para>
    ///
    /// <para><b>Nothing is cancelled, and the count is what bounds it instead</b> — see
    /// <see cref="SendAsync"/> for why a cancel here can cost the user their session.</para>
    /// </summary>
    static async Task BeatAsync(
            IFirstRunFlowChannel channel, string serverUrl, string flowId, TimeProvider clock,
            TimeSpan interval, CancellationTokenSource stopping) {
        var ct = stopping.Token;

        using var timer = new PeriodicTimer(interval, clock);

        var inFlight = new List<Task<FirstRunHeartbeatOutcome>>(MaxInFlight);

        var quietUntil  = DateTimeOffset.MinValue;
        var unavailable = 0;

        try {
            while (!ct.IsCancellationRequested) {
                for (var i = inFlight.Count - 1; i >= 0; i--) {
                    if (!inFlight[i].IsCompleted) continue;

                    var outcome = await inFlight[i];

                    inFlight.RemoveAt(i);

                    if (outcome.StatusCode is 404 or 405) {
                        if (++unavailable >= UnavailableBeforeBackingOff) {
                            quietUntil  = clock.GetUtcNow() + UnavailableBackoff;
                            unavailable = 0;
                        }
                    } else {
                        unavailable = 0;
                    }

                    if (outcome.StatusCode is 429) {
                        var asked = outcome.RetryAfter ?? ThrottleBackoff;

                        quietUntil = clock.GetUtcNow()
                                   + (asked > MaxThrottleBackoff ? MaxThrottleBackoff : asked);
                    }
                }

                if (inFlight.Count < MaxInFlight && clock.GetUtcNow() >= quietUntil)
                    inFlight.Add(SendAsync(channel, serverUrl, flowId));

                if (!await timer.WaitForNextTickAsync(ct)) return;
            }
        } catch (OperationCanceledException) {
            // The stop.
        } catch (ObjectDisposedException) {
            // Dispose raced the tick this loop was entering. Same outcome as the cancel, and the reason
            // the source can be disposed there at all: nothing below this line reads it.
        }
    }

    /// <summary>
    /// One beat, issued with no cancellation and swallowing everything.
    ///
    /// <para><b>The token is withheld deliberately, and it is the load-bearing rule here.</b> The beat
    /// rides the setup client, whose 401 handler recovers the credential: WorkOS rotates a single-use
    /// refresh token — a call that cannot be cancelled — and the replacement is persisted afterwards
    /// under whatever token the request carried. A cancel landing between the two spends the credential
    /// server-side and never writes what replaced it, logging the user out mid-setup. What ends a beat is
    /// the client's own timeout; what ends the LOOP is the token, one level up.</para>
    ///
    /// <para>The channel call is invoked inside, not passed in: <see cref="IFirstRunFlowChannel"/> is
    /// public and nothing obliges an implementation to be <c>async</c>, so a synchronous throw evaluated
    /// as an argument would escape this and stop the loop for the rest of the leg.</para>
    /// </summary>
    static async Task<FirstRunHeartbeatOutcome> SendAsync(
            IFirstRunFlowChannel channel, string serverUrl, string flowId) {
        try {
            return await channel.HeartbeatAsync(serverUrl, flowId, CancellationToken.None);
        } catch (Exception) {
            // Best effort, by construction. A status is read for what it says, never for whether it worked.
            return new(0);
        }
    }
}
