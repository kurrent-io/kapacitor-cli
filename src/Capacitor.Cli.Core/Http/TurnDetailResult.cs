namespace Capacitor.Cli.Core.Http;

/// <summary>What reading a single turn's detail can mean. <see cref="NotFound"/> covers both an
/// unknown session and an unknown turn index — the caller already holds both to phrase the message.</summary>
public abstract record TurnDetailResult {
    public sealed record Found(string Json) : TurnDetailResult;

    public sealed record NotFound : TurnDetailResult;
}
