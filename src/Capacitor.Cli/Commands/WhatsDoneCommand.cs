using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

sealed class WhatsDoneCommand(
        ConfigRoot config, ProfileContext profiles, UserHome home, ICapacitorHttpClient http) {
    public async Task<int> HandleGenerateWhatsDone(string baseUrl, string sessionId, string vendor = "claude") {
        // Redirect output to log file (same pattern as WatchCommand)
        var logDir = config.Path("logs");
        Directory.CreateDirectory(logDir);
        var logPath   = Path.Combine(logDir, $"{sessionId}-whatsdone.log");
        var logWriter = new StreamWriter(logPath, append: true) { AutoFlush = true };
        Console.SetOut(logWriter);
        Console.SetError(logWriter);

        try {
            return await GenerateForSessionAsync(baseUrl, sessionId, Log, vendor);
        } finally {
            await logWriter.DisposeAsync();
        }
    }

    /// <summary>
    /// Core what's-done generation logic, callable without Console redirection.
    /// Uses the provided <paramref name="log"/> callback for diagnostics.
    /// </summary>
    /// <param name="vendor">"claude" (default) or "codex" — picks the headless CLI runner.</param>
    public async Task<int> GenerateForSessionAsync(string baseUrl, string sessionId, Action<string> log, string vendor = "claude") {
        log($"Generating what's-done summary for session {sessionId}");

        // The background lane, not the interactive one: both entry points are unattended — one has
        // already redirected stderr into a log file, the other runs inside a silent import loop — so a
        // re-auth hint reaches nobody and multiplies by the session count.
        using var httpClient = await http.ForBackgroundAsync();

        // 1. Fetch session recap
        string recapText;

        try {
            using var resp = await httpClient.GetWithRetryAsync($"{baseUrl}/api/sessions/{sessionId}/recap");

            if (!resp.IsSuccessStatusCode) {
                log($"Failed to fetch recap: HTTP {(int)resp.StatusCode}");

                return 1;
            }

            var json    = await resp.Content.ReadAsStringAsync();
            var entries = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.ListRecapEntry);

            if (entries is null || entries.Count == 0) {
                log("No recap entries found, skipping summary generation");

                return 0;
            }

            recapText = FormatRecapAsText(entries);

            if (string.IsNullOrWhiteSpace(recapText)) {
                log("Recap text is empty after formatting, skipping");

                return 0;
            }
        } catch (HttpRequestException ex) {
            log($"Server unreachable: {ex.Message}");

            return 1;
        }

        // 2. Call the headless CLI for the matching vendor to generate the summary.
        // Codex-vendor sessions go through `codex exec` so the summary model
        // matches the one that actually produced the work.
        log($"Calling {vendor} to generate summary...");
        log($"Recap text: {recapText.Length} chars");

        var prompt = EmbeddedResources.Load("prompt-whats-done.txt") + recapText;

        var result = vendor == "codex"
            ? await CodexCliRunner.RunAsync(prompt, TimeSpan.FromSeconds(90), log, profiles.Resolution.Profile)
            : await ClaudeCliRunner.RunAsync(prompt, TimeSpan.FromSeconds(90), log, profiles.Resolution.Profile, home,
                systemPrompt: TitleGeneration.HeadlessSummarizerSystemPrompt);

        if (result is null) {
            log($"{vendor} returned empty or failed");

            return 1;
        }

        log($"Summary generated ({result.Result.Length} chars), model={result.Model}, input={result.InputTokens}, output={result.OutputTokens}");

        // 3. POST result back to server
        var payload = new WhatsDonePayload {
            SessionId        = sessionId,
            Content          = result.Result,
            Model            = result.Model,
            InputTokens      = result.InputTokens,
            OutputTokens     = result.OutputTokens,
            CacheReadTokens  = result.CacheReadTokens,
            CacheWriteTokens = result.CacheWriteTokens
        };

        var       payloadJson = JsonSerializer.Serialize(payload, CapacitorJsonContext.Default.WhatsDonePayload);
        using var httpContent = new StringContent(payloadJson, Encoding.UTF8, "application/json");

        try {
            using var postResp = await httpClient.PostWithRetryAsync($"{baseUrl}/hooks/whats-done", httpContent);

            log(
                postResp.IsSuccessStatusCode
                    ? "Successfully posted what's-done summary"
                    : $"POST failed: HTTP {(int)postResp.StatusCode}"
            );
        } catch (HttpRequestException ex) {
            log($"Server unreachable for POST: {ex.Message}");

            return 1;
        }

        return 0;
    }

    static string FormatRecapAsText(List<RecapEntry> entries) {
        var sb = new StringBuilder();

        foreach (var entry in entries) {
            switch (entry.Type) {
                case "plan":
                    sb.AppendLine("## Plan");
                    sb.AppendLine(entry.Content);
                    sb.AppendLine();

                    break;
                case "user_prompt":
                    sb.AppendLine("## User Prompt");
                    sb.AppendLine(entry.Content);
                    sb.AppendLine();

                    break;
                case "assistant_text":
                    sb.AppendLine("## Assistant");
                    sb.AppendLine(entry.Content);
                    sb.AppendLine();

                    break;
                case "write":
                    sb.AppendLine($"- Write: {entry.FilePath ?? "unknown"}");

                    break;
                case "edit":
                    sb.AppendLine($"- Edit: {entry.FilePath ?? "unknown"}");

                    break;
                case "whats_done":
                    sb.AppendLine("## What's Done (previous summary)");
                    sb.AppendLine(entry.Content);
                    sb.AppendLine();

                    break;
            }
        }

        // Truncate to avoid exceeding claude's input limits
        var text = sb.ToString();

        return text.Length > 30_000 ? text[^30_000..] : text;
    }

    static void Log(string message) => Console.Error.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] [whats-done] {message}");
}
