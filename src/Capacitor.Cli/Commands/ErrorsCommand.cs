using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

class ErrorsCommand(ISessionsApi sessionsApi) {
    public async Task<int> HandleErrors(string sessionId, bool chain) {
        ErrorsResult result;

        try {
            result = await sessionsApi.GetErrorsAsync(sessionId, chain);
        } catch (CapacitorApiException ex) {
            await Console.Error.WriteLineAsync(ex.Message);

            return 1;
        }

        if (result is ErrorsResult.NotFound) {
            await Console.Error.WriteLineAsync($"Session not found: {sessionId}");

            return 1;
        }

        var errors = ((ErrorsResult.Found)result).Errors;

        if (errors.Count == 0) {
            await Console.Out.WriteLineAsync("No errors found.");

            return 0;
        }

        await Console.Out.WriteLineAsync($"Found {errors.Count} error(s):\n");

        foreach (var error in errors) {
            var label    = error.SessionSlug ?? error.SessionId;
            var agentTag = error.AgentId is not null ? $" (agent {error.AgentId})" : "";
            var tool     = error.ToolName ?? "unknown";
            await Console.Out.WriteLineAsync($"  [{label}]{agentTag} #{error.EventNumber} {tool}");
            await Console.Out.WriteLineAsync($"    {error.Error}");
            await Console.Out.WriteLineAsync();
        }

        return 0;
    }
}
