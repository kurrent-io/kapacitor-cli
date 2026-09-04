namespace Capacitor.Cli.Core;

/// <summary>The applicability gate's platform vocabulary: macos / linux / windows.</summary>
public static class HostPlatform {
    public static string? Normalized =>
        OperatingSystem.IsMacOS()   ? "macos"
      : OperatingSystem.IsLinux()   ? "linux"
      : OperatingSystem.IsWindows() ? "windows"
      : null;
}
