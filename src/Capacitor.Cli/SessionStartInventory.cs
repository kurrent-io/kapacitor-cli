using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli;

/// <summary>
/// Stamps this machine's harness inventory and platform onto a SessionStart body, so a daemonless
/// machine still reports them through the hook carrier. The inventory is serialized through the same
/// <see cref="CapacitorJsonContext"/> as the daemon's copy, so both carriers are byte-identical.
/// Never throws — a probe failure just omits the field (must never break a hook).
/// </summary>
static class SessionStartInventory {
    public static void Stamp(JsonObject body, ConfigRoot config, UserHome home) {
        try {
            var inv  = HarnessInventory.EvaluateCurrent(config, HarnessRegistry.FromEnvironment(home));
            var json = JsonSerializer.Serialize(inv, CapacitorJsonContext.Default.HarnessInventory);
            body["harness_inventory"] = JsonNode.Parse(json);
        } catch {
            // best-effort metadata — never break a hook
        }
        // The CLI's own OS, feeding the server's live applicability gate. Independent of the
        // inventory probe (its own best-effort boundary — an inventory failure must not cost the
        // platform axis), and deliberately no path/heuristic inference: omitted when unrecognized,
        // and unknown EXCLUDES platform-restricted facts server-side, which beats a wrong guess.
        try {
            if (HostPlatform.Normalized is { } platform) body["platform"] = platform;
        } catch {
            // best-effort metadata — never break a hook
        }
    }
}
