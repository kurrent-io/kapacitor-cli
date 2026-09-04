namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// What a mint attempt produced: a bearer and the lifetime WorkOS put on it, or the reason it
/// produced neither. <c>ExpiresIn</c> is nominal seconds and may be absent — RFC 6749 does not
/// require the field — so a zero is "unstated", not "already expired".
/// </summary>
public readonly record struct MachineTokenMint(string? Token, int ExpiresIn, string? Problem);
