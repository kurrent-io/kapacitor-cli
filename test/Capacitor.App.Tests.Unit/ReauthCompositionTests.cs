using System.Reactive.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.Services.Onboarding;
using Capacitor.App.Views;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.App.Tests.Unit;

/// The steady-state re-auth graph is the wizard's sign-in step alone, pinned to the server the
/// profile already targets — a re-auth must never repoint server_url at a different origin.
public class ReauthCompositionTests {
    const string ServerUrl = "https://acme.example";

    static AuthResult.Committed Committed() =>
        new("default", ServerUrl, AuthProvider.GitHubApp, "alice", []);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Sign_in_runs_a_paste_intent_pinned_to_the_configured_server() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var config = new TempConfigRoot();
            ConnectIntent? seen = null;

            var graph = ReauthComposition.Build(
                config.Root, AuthFixtures.NewTokenStore(config.Root), new PlainHttpClientFactory(),
                new AuthProxyClient(new HttpClient()), new(new PlainHttpClientFactory()), new(new PlainHttpClientFactory()),
                "default", ServerUrl,
                WizardComposition.BuildBridges(action => action(), new(new HttpClient())),
                new ConsentFlipClaims(config.Root),
                new AppStateStore(config.PathTo("app-state.json")),
                new RecordingOpener(),
                _ => (intent, _) => {
                    seen = intent;
                    return Task.FromResult<AuthResult>(Committed());
                });

            await graph.SignIn.SignInCommand.Execute().ToTask();

            await Assert.That(seen).IsEqualTo(new ConnectIntent.Paste(ServerUrl));
            await Assert.That(graph.SignIn.Satisfied).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Closing_mid_attempt_cancels_it_and_quiesces() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var config = new TempConfigRoot();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var graph = ReauthComposition.Build(
                config.Root, AuthFixtures.NewTokenStore(config.Root), new PlainHttpClientFactory(),
                new AuthProxyClient(new HttpClient()), new(new PlainHttpClientFactory()), new(new PlainHttpClientFactory()),
                "default", ServerUrl,
                WizardComposition.BuildBridges(action => action(), new(new HttpClient())),
                new ConsentFlipClaims(config.Root),
                new AppStateStore(config.PathTo("app-state.json")),
                new RecordingOpener(),
                _ => async (_, ct) => {
                    started.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                    return Committed();
                });

            var run = graph.SignIn.SignInCommand.Execute().ToTask();
            await started.Task;

            await graph.CloseAsync(CancellationToken.None);
            await run;

            await Assert.That(graph.SignIn.Satisfied).IsFalse();
            await graph.Auth.QuiescedAsync(); // completes only once no attempt is live
        });
    }

    /// The dialog renders the SAME sign-in view the wizard shows, and entering it announces the
    /// pinned server — the user must see where they are signing in to.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_dialog_hosts_the_wizard_sign_in_view_announcing_the_server() {
        var (buttonFound, status) = await AvaloniaSession.DispatchAsync(() => {
            using var config = new TempConfigRoot();
            var graph = ReauthComposition.Build(
                config.Root, AuthFixtures.NewTokenStore(config.Root), new PlainHttpClientFactory(),
                new AuthProxyClient(new HttpClient()), new(new PlainHttpClientFactory()), new(new PlainHttpClientFactory()),
                "default", ServerUrl,
                WizardComposition.BuildBridges(action => action(), new(new HttpClient())),
                new ConsentFlipClaims(config.Root),
                new AppStateStore(config.PathTo("app-state.json")),
                new RecordingOpener(),
                _ => (_, _) => Task.FromResult<AuthResult>(Committed()));

            var window = new SignInWindow { DataContext = graph.SignIn };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var button = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "SignInButton");
            var text = window.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => t.Name == "SignInStatusText")?.Text;

            window.Close();
            Dispatcher.UIThread.RunJobs();
            return (button is not null, text);
        });

        await Assert.That(buttonFound).IsTrue();
        await Assert.That(status).IsEqualTo(ServerUrl);
    }
}
