using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Spectre.Console;

namespace Capacitor.Cli.Commands;

/// <summary>
/// <c>kcap projects</c> / <c>kcap project &lt;slug&gt;</c> — read-only views over the server's
/// <c>/api/projects</c> endpoints. Every route 403s with <c>projects_not_in_plan</c> on the Free plan
/// (see <see cref="CliProjectError"/>).
/// </summary>
class ProjectsCommand(IProjectsApi projectsApi) {
    public async Task<int> HandleList() {
        ProjectsResult result;

        try {
            result = await projectsApi.GetProjectsAsync();
        } catch (CapacitorApiException ex) {
            await Console.Error.WriteLineAsync(ex.Message);

            return 1;
        }

        if (result is ProjectsResult.Forbidden(var errorCode)) {
            return ReportForbidden(errorCode);
        }

        var projects = ((ProjectsResult.Found)result).Projects;

        if (projects.Count == 0) {
            await Console.Out.WriteLineAsync("No projects found.");

            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Slug");
        table.AddColumn("Name");
        table.AddColumn("Repos");
        table.AddColumn("Members");
        table.AddColumn("Your role");

        foreach (var project in projects) {
            table.AddRow(
                Markup.Escape(project.Slug),
                Markup.Escape(project.Name),
                project.RepoCount.ToString(),
                project.MemberCount.ToString(),
                Markup.Escape(FormatRole(project.ViewerMembership, project.ViewerPending))
            );
        }

        AnsiConsole.Write(table);

        return 0;
    }

    public async Task<int> HandleDetail(string slug) {
        ProjectResult result;

        try {
            result = await projectsApi.GetProjectAsync(slug);
        } catch (CapacitorApiException ex) {
            await Console.Error.WriteLineAsync(ex.Message);

            return 1;
        }

        if (result is ProjectResult.Forbidden(var errorCode)) {
            return ReportForbidden(errorCode);
        }

        if (result is ProjectResult.NotFound) {
            await Console.Error.WriteLineAsync("Project not found.");

            return 1;
        }

        var project = ((ProjectResult.Found)result).Project;

        await Console.Out.WriteLineAsync($"{project.Name} ({project.Slug})");

        if (!string.IsNullOrWhiteSpace(project.Description)) {
            await Console.Out.WriteLineAsync(project.Description);
        }

        await Console.Out.WriteLineAsync($"  Owner:     {project.OwnerUserId}");
        await Console.Out.WriteLineAsync($"  Your role: {FormatRole(project.ViewerMembership, project.ViewerPending)}");

        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync($"Repos ({project.Repos.Count}):");

        if (project.Repos.Count == 0) {
            await Console.Out.WriteLineAsync("  (none)");
        } else {
            foreach (var repo in project.Repos) {
                await Console.Out.WriteLineAsync($"  {repo.RepoSlug}");
            }
        }

        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync($"Members ({project.Members.Count}):");

        if (project.Members.Count == 0) {
            await Console.Out.WriteLineAsync("  (none)");
        } else {
            foreach (var member in project.Members) {
                await Console.Out.WriteLineAsync($"  {member.DisplayName} ({member.MemberKind})");
            }
        }

        if (project.JoinRequests.Count > 0) {
            await Console.Out.WriteLineAsync();
            await Console.Out.WriteLineAsync($"Join requests ({project.JoinRequests.Count}):");

            foreach (var request in project.JoinRequests) {
                await Console.Out.WriteLineAsync($"  {request.UserId} — {request.Direction} ({request.RequestedAt:u})");
            }
        }

        return 0;
    }

    /// <summary>
    /// Every <c>/api/projects*</c> route 403s identically when the tenant plan doesn't include
    /// projects (Free). Falls back to a generic message for any other 403 shape.
    /// </summary>
    static int ReportForbidden(string? errorCode) {
        if (errorCode == "projects_not_in_plan") {
            Console.Error.WriteLine("Projects require the Team or Enterprise plan.");

            return 1;
        }

        Console.Error.WriteLine("Forbidden.");

        return 1;
    }

    static string FormatRole(string viewerMembership, string? viewerPending) => viewerMembership switch {
        "owner"  => "owner",
        "member" => "member",
        _ => viewerPending switch {
            "invite"  => "pending invite",
            "request" => "pending request",
            _         => "—"
        }
    };
}
