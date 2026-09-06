namespace Capacitor.Cli.Core;

/// <summary>
/// The configured server URL cannot be sent to. Raised where a caller has a user to tell and an exit
/// code to return, never from a send path.
///
/// <para>Deliberately a distinct type so an audit can find every <c>catch</c> that must re-throw it
/// rather than swallow it and continue as though the server were reachable. A bare <c>catch</c> whose
/// fallback branch writes output or persists state would turn a loud failure into a silent wrong
/// one.</para>
/// </summary>
public sealed class UnusableServerUrlException(string hint) : Exception(hint);
