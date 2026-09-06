using Capacitor.Cli.Commands.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace Capacitor.Cli.Commands;

public static class CommandServices {
    /// <summary>
    /// Registers every command the dispatch switch resolves. Commands are transient: a run
    /// dispatches one, and holding them would keep a command's own state alive past its verb.
    /// </summary>
    public static IServiceCollection AddCapacitorCommands(this IServiceCollection services) {
        services.AddTransient<AgentCommand>();
        services.AddTransient<CleanupCommand>();
        services.AddTransient<ConfigCommand>();
        services.AddTransient<CurateCommand>();
        services.AddTransient<DaemonCommands>();
        services.AddTransient<ErrorsCommand>();
        services.AddTransient<EvalCommand>();
        services.AddTransient<FeedbackCommand>();
        services.AddTransient<HarnessCommand>();
        services.AddTransient<IgnoreCommand>();
        services.AddTransient<ImportCommand>();
        services.AddTransient<LoginCommand>();
        services.AddTransient<MachineCommand>();
        services.AddTransient<PluginCommand>();
        services.AddTransient<ProfileCommand>();
        services.AddTransient<ProjectsCommand>();
        services.AddTransient<RecapCommand>();
        services.AddTransient<RemapCommand>();
        services.AddTransient<ReportVersionCommand>();
        services.AddTransient<ReposCommand>();
        services.AddTransient<ReviewCommand>();
        services.AddTransient<SessionsCommand>();
        services.AddTransient<SetupCommand>();
        services.AddTransient<SkillsCommand>();
        services.AddTransient<StatusCommand>();
        services.AddTransient<McpFlowResultServer>();
        services.AddTransient<McpFlowsServer>();
        services.AddTransient<McpMemoryServer>();
        services.AddTransient<McpSessionsServer>();
        services.AddTransient<McpWorkItemsServer>();
        services.AddTransient<McpAnalyticsServer>();
        services.AddTransient<McpReviewServer>();
        services.AddTransient<McpJudgeServer>();
        services.AddTransient<UninstallCommand>();
        services.AddTransient<UpdateCommand>();
        services.AddTransient<UseCommand>();
        services.AddTransient<ValidatePlanCommand>();
        services.AddTransient<WatchCommand>();
        services.AddTransient<WhatsDoneCommand>();
        services.AddTransient<WhoamiCommand>();

        services.AddTransient<AntigravityHookCommand>();
        services.AddTransient<ClaudeHookCommand>();
        services.AddTransient<CodexHookCommand>();
        services.AddTransient<CopilotFinalizeDrainCommand>();
        services.AddTransient<CopilotHookCommand>();
        services.AddTransient<CursorHookCommand>();
        services.AddTransient<GeminiHookCommand>();
        services.AddTransient<KiroHookCommand>();
        services.AddTransient<OpenCodeHookCommand>();
        services.AddTransient<PiHookCommand>();

        return services;
    }
}
