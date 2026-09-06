using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Capacitor.Cli.Core.Auth;
using Duende.IdentityModel.OidcClient.Browser;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>
/// Who shuts the loopback listener down, and does every construction site wire up the join.
///
/// <para>Construction used to be free to inline as an argument, because <c>using var</c> inside the
/// browser handled teardown. It no longer does — the listener outlives <c>InvokeAsync</c> for the
/// return-hop wait — so an unnamed instance holds the port for the life of the process, and a site
/// that omits the collaborator serves today's dead-end page with the whole feature silently absent.
/// Neither failure throws or turns a test red.</para>
///
/// <para><b>Scope: every <c>.cs</c> file under <c>src/</c>.</b> An earlier version of this guard
/// named the two files that happened to construct one, and went blind the moment the onboarding
/// wizard added a third site in a new file — passing while scanning a file that no longer had a
/// site at all. A guard whose whole purpose is to catch "someone added one of these somewhere"
/// cannot encode today's file layout.</para>
///
/// <para>Guarded at source because the behavioural form would bind a port and launch a browser.
/// <see cref="FindOwnershipViolations"/> takes its root as a parameter so the scanner self-tests
/// below can prove it actually detects, against a synthetic fixture rather than real source.</para>
///
/// <para><b>Two checks, because pattern matching over source can always be out-spelled.</b> Three
/// review rounds each out-spelled it once, so the patterns now ignore trivia — whitespace, line
/// breaks, block comments — at EVERY junction (see <see cref="Trivia"/>), accept <c>global::</c> and
/// dotted qualification, and resolve <c>using</c> aliases. The spellings that got through, all of
/// which compile: <c>new LoopbackBrowser /* c */ (…)</c>, a line break before the paren, a
/// <c>global::</c>-qualified name, <c>new LB(…)</c> behind an alias, and
/// <c>using LB = …LoopbackBrowser /* c */;</c>.
/// <see cref="Only_the_known_files_name_the_browser_type_at_all"/> additionally pins the set of files
/// allowed to NAME the type at all, which catches a construction in a file nobody thought about.</para>
///
/// <para><b>What this still does not guarantee.</b> An earlier version of this comment claimed the
/// file-set check was the backstop no spelling could slip past. That was wrong, and a reviewer
/// disproved it with a two-line refactor: a <c>using</c> alias inside an ALREADY-allowlisted file
/// leaves the file set unchanged while hiding the construction from a name-based pattern. Aliases are
/// handled now, but the shape of that hole is general — a factory method, a generic
/// <c>Activator</c>-style construction, or reflection inside an allowlisted file would each still be
/// invisible. Nothing text-based closes that; only a semantic model would. This guard is a high-value
/// tripwire for the mistakes people actually make, not a proof.</para>
/// </summary>
public class LoopbackOwnershipTests {
    // The code exchange rides the sign-in lane; against a stub, an unconfigured client is what
    // that lane resolves to.
    static readonly GitHubOAuthClient Github = new(new PlainHttpClientFactory());

    /// <summary>
    /// Whitespace, line breaks and block comments — what the compiler ignores between two tokens, and
    /// therefore what this file's patterns must ignore too, at EVERY junction.
    /// <para>Shared deliberately. The alias pattern below originally spelled its own trailing
    /// separator as a bare <c>\s*</c> while the construction pattern tolerated comments, so
    /// <c>using LB = …LoopbackBrowser /* c */;</c> compiled and defeated alias recognition. One
    /// definition, used everywhere, is what stops the two drifting apart again.</para>
    /// </summary>
    const string Trivia = @"\s*(?:/\*.*?\*/\s*)*";

    /// <summary>A keyword, requiring a real separator after it so <c>newLB(</c> is not a match while
    /// <c>new LB(</c> and <c>new/* c */LB(</c> both are.</summary>
    const string Separated = @"(?=[\s/])";

    /// <summary>Optional <c>global::</c> and dotted namespace qualification before a type name.</summary>
    const string Qualifier = @"(?:global\s*::\s*)?(?:[A-Za-z_]\w*\s*\.\s*)*";

    static readonly Regex Construction = ConstructionOf("LoopbackBrowser");

    /// <summary>A construction of <paramref name="typeName"/>, however the trivia falls.</summary>
    static Regex ConstructionOf(string typeName) => new(
        $@"new{Separated}{Trivia}{Qualifier}{Regex.Escape(typeName)}{Trivia}\(",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// A <c>using X = …LoopbackBrowser;</c> alias. Aliasing defeats a name-based scan while leaving
    /// the type named in the file, so it slips past the filename allowlist too — the spelling that
    /// beat both checks at once.
    /// </summary>
    static readonly Regex Alias = new(
        $@"^{Trivia}using{Separated}{Trivia}(?<alias>[A-Za-z_]\w*){Trivia}={Trivia}{Qualifier}LoopbackBrowser{Trivia};",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.Singleline);

    /// <summary>An externally supplied browser that would notice being disposed by the callee.</summary>
    sealed class DisposableFakeBrowser(string query) : IBrowser, IDisposable {
        public bool Disposed { get; private set; }

        public Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken ct = default) =>
            Task.FromResult(new BrowserResult { ResultType = BrowserResultType.Success, Response = query });

        public void Dispose() => Disposed = true;
    }

    // Disposing an injected browser would tear down a test's stand-in mid-test — or, later, a
    // caller's shared instance. The state mismatch below makes the flow return null without any
    // network call; what matters is what happened to `fake`.
    [Test]
    public async Task An_injected_browser_is_never_disposed_by_the_callee() {
        var fake = new DisposableFakeBrowser("?code=abc&state=mismatch");

        var token = await OAuthLoginFlow.RunGitHubBrowserFlowAsync(
            Github, "client-id", "http://127.0.0.1:1/exchange", new RecordingBrowser(),
            browser: fake, timeout: TimeSpan.FromSeconds(1));

        await Assert.That(token).IsNull();
        await Assert.That(fake.Disposed).IsFalse();
    }

    /// <summary>Walks up from this file's own compile-time path, so it is independent of the
    /// runner's working directory.</summary>
    static string RepoRoot([CallerFilePath] string here = "") {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Capacitor.slnx")))
            dir = Path.GetDirectoryName(dir);

        if (dir is null)
            throw new InvalidOperationException($"Could not locate repo root (Capacitor.slnx) walking up from {here}");

        return dir;
    }

    /// <summary>
    /// Every construction site under <paramref name="srcRoot"/>, as
    /// <c>(file, line, statement, arguments)</c>. Skips occurrences on a <c>//</c> line, which are
    /// documentation rather than code.
    /// <para>The statement is taken back to the nearest <c>;</c> <c>{</c> or <c>}</c> — the ternary
    /// form the two flows use spans lines, with the <c>using</c> declaration above the <c>new</c>,
    /// so a per-line test would reject the correct code and a substring of the same line would
    /// accept an inline argument.</para>
    /// </summary>
    internal static List<(string File, int Line, string Statement, string Arguments)> FindSites(string srcRoot) {
        var sites = new List<(string, int, string, string)>();

        foreach (var file in SourceFiles(srcRoot)) {
            var source = File.ReadAllText(file);

            // The type's own name, plus every local alias for it.
            var patterns = new List<Regex> { Construction };
            foreach (var alias in Alias.Matches(source).Cast<Match>())
                patterns.Add(ConstructionOf(alias.Groups["alias"].Value));

            foreach (var match in patterns.SelectMany(p => p.Matches(source).Cast<Match>())) {
                var at        = match.Index;
                var lineStart = source.LastIndexOf('\n', at) + 1;

                if (source[lineStart..at].Contains("//", StringComparison.Ordinal)) continue;

                var start = source.LastIndexOfAny([';', '{', '}'], at) + 1;
                var line  = source[..at].Count(c => c == '\n') + 1;

                // match.Index + match.Length - 1 is the '(' the regex consumed, wherever the trivia
                // put it, so the argument list is read from the real paren rather than a fixed offset.
                sites.Add((Path.GetFileName(file), line, source[start..at],
                           ArgumentsAt(source, match.Index + match.Length - 1)));
            }
        }

        return sites;
    }

    /// <summary>
    /// Hand-written <c>.cs</c> under <paramref name="root"/>. <c>bin</c>/<c>obj</c> are excluded
    /// because they sit INSIDE <c>src/</c>: they hold generated sources, and a generator that ever
    /// emitted a copy of a flow would have the scanner counting the same site twice.
    /// </summary>
    static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                          .Any(segment => segment is "bin" or "obj"));

    /// <summary>The argument list, paren-balanced so a nested call inside it doesn't truncate.</summary>
    static string ArgumentsAt(string source, int open) {
        var depth = 0;

        for (var i = open; i < source.Length; i++) {
            if (source[i] == '(') depth++;
            else if (source[i] == ')' && --depth == 0) return source[(open + 1)..i];
        }

        return source[open..];
    }

    /// <summary>
    /// One violation string per site that either leaves nobody to dispose the listener or omits the
    /// join collaborator. A <c>using</c> declaration is the accepted form: it names the instance and
    /// scopes its teardown, and on a nullable local it disposes exactly when the local is the one we
    /// built.
    /// </summary>
    internal static List<string> FindOwnershipViolations(string srcRoot) {
        var violations = new List<string>();

        foreach (var (file, line, statement, arguments) in FindSites(srcRoot)) {
            if (!statement.Contains("using ", StringComparison.Ordinal))
                violations.Add($"{file}:{line}: constructed outside a `using` declaration — {Squash(statement)}");

            if (!arguments.Contains("SetupJoin.Loopback", StringComparison.Ordinal))
                violations.Add($"{file}:{line}: does not pass the join collaborator — {Squash(arguments)}");
        }

        return violations;
    }

    static string Squash(string text) => string.Join(' ', text.Split('\n', StringSplitOptions.TrimEntries)).Trim();

    // === The real guard: scans this repo's actual src/ tree ===

    [Test]
    public async Task Every_construction_site_is_owned_and_passes_the_join() {
        var violations = FindOwnershipViolations(Path.Combine(RepoRoot(), "src"));

        await Assert.That(violations).IsEmpty();
    }

    // A guard that finds nothing to check reports success, so the floor is asserted rather than
    // assumed. Both sites live in OAuthLoginFlow now — the GitHub flow and the WorkOS ladder —
    // after upstream moved construction out of OnboardingFacade and into the ladder. The floor
    // dropped from three sites in two files to two in one when that happened, which is exactly the
    // kind of change worth re-deriving by hand rather than letting a scan quietly go empty.
    [Test]
    public async Task The_scan_actually_reaches_the_construction_sites() {
        var sites = FindSites(Path.Combine(RepoRoot(), "src"));

        await Assert.That(sites.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(sites.Select(s => s.File).Distinct().Count()).IsGreaterThanOrEqualTo(1);
    }

    // === Scanner self-tests: prove the detector detects, and that the accepted forms are accepted,
    // against a synthetic fixture directory rather than real source. ===

    [Test]
    public async Task Scanner_accepts_both_forms_the_real_flows_use() {
        using var tmp = new TempDir();

        tmp.CreateFile("Owned.cs", [
            "namespace Fixture;",
            "static class Owned {",
            "    static void Simple() {",
            "        using var browser = new LoopbackBrowser(progress: progress, join: SetupJoin.Loopback);",
            "    }",
            "    static void NullableTernary(object? injected) {",
            "        using LoopbackBrowser? created =",
            "            injected is null ? new LoopbackBrowser(progress: progress, join: SetupJoin.Loopback) : null;",
            "    }",
            "}",
        ]);

        await Assert.That(FindOwnershipViolations(tmp.Path)).IsEmpty();
        await Assert.That(FindSites(tmp.Path).Count).IsEqualTo(2);
    }

    [Test]
    public async Task Scanner_flags_an_inline_construction_that_nobody_disposes() {
        using var tmp = new TempDir();

        tmp.CreateFile("Leaked.cs", [
            "namespace Fixture;",
            "static class Leaked {",
            "    static void Go() {",
            "        var options = new OidcClientOptions {",
            "            Browser = new LoopbackBrowser(progress: progress, join: SetupJoin.Loopback),",
            "        };",
            "    }",
            "}",
        ]);

        var violations = FindOwnershipViolations(tmp.Path);

        await Assert.That(violations.Count).IsEqualTo(1);
        await Assert.That(violations[0]).Contains("outside a `using` declaration");
    }

    [Test]
    public async Task Scanner_flags_a_site_that_omits_the_join_collaborator() {
        using var tmp = new TempDir();

        tmp.CreateFile("Joinless.cs", [
            "namespace Fixture;",
            "static class Joinless {",
            "    static void Go() {",
            "        using var browser = new LoopbackBrowser(progress: progress);",
            "    }",
            "}",
        ]);

        var violations = FindOwnershipViolations(tmp.Path);

        await Assert.That(violations.Count).IsEqualTo(1);
        await Assert.That(violations[0]).Contains("does not pass the join collaborator");
    }

    // The blindness this guard was rewritten to fix: a fourth site in a file nobody thought to
    // list. Enumeration finds it; a name list would not.
    [Test]
    public async Task Scanner_finds_a_site_in_a_file_it_was_never_told_about() {
        using var tmp = new TempDir();

        tmp.CreateFile("Nested/BrandNewFacade.cs", [
            "namespace Fixture.Nested;",
            "static class BrandNewFacade {",
            "    static void Go() {",
            "        var browser = new LoopbackBrowser(progress: progress);",
            "    }",
            "}",
        ]);

        var violations = FindOwnershipViolations(tmp.Path);

        await Assert.That(violations.Count).IsEqualTo(2);
        await Assert.That(violations.TrueForAll(v => v.Contains("BrandNewFacade.cs:4"))).IsTrue();
    }

    // Trivia between the type name and the argument list. All three compile, and a scanner looking
    // for the literal token "new LoopbackBrowser(" sees none of them — so an unowned, joinless site
    // spelled any of these ways would leave every assertion in this file green.
    [Test]
    [Arguments("        var browser = new LoopbackBrowser /* built here */ (progress: progress);")]
    [Arguments("        var browser = new LoopbackBrowser (progress: progress);")]
    [Arguments("        var browser = new global::Capacitor.Cli.Core.Auth.LoopbackBrowser(progress: progress);")]
    public async Task Scanner_finds_a_construction_however_it_is_spelled(string construction) {
        using var tmp = new TempDir();

        tmp.CreateFile("Sneaky.cs", [
            "namespace Fixture;",
            "static class Sneaky {",
            "    static void Go() {",
            construction,
            "    }",
            "}",
        ]);

        await Assert.That(FindSites(tmp.Path).Count).IsEqualTo(1);
        await Assert.That(FindOwnershipViolations(tmp.Path).Count).IsEqualTo(2);
    }

    // A construction split across lines — the argument list on the line after the type name.
    [Test]
    public async Task Scanner_finds_a_construction_split_across_lines() {
        using var tmp = new TempDir();

        tmp.CreateFile("Wrapped.cs", [
            "namespace Fixture;",
            "static class Wrapped {",
            "    static void Go() {",
            "        var browser = new LoopbackBrowser",
            "            (progress: progress);",
            "    }",
            "}",
        ]);

        await Assert.That(FindSites(tmp.Path).Count).IsEqualTo(1);
        await Assert.That(FindOwnershipViolations(tmp.Path).Count).IsEqualTo(2);
    }

    // The spelling-independent backstop. Whatever syntax a new lane uses, it has to NAME the type,
    // so the set of files allowed to mention it is asserted — that is what catches a construction
    // this file's pattern matching does not anticipate.
    [Test]
    public async Task Only_the_known_files_name_the_browser_type_at_all() {
        var naming = SourceFiles(Path.Combine(RepoRoot(), "src"))
            .Where(f => File.ReadAllText(f).Contains("LoopbackBrowser", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .Order()
            .ToArray();

        await Assert.That(naming).IsEquivalentTo(BrowserNamingFiles.Order().ToArray())
            .Because("a file newly naming the type may be constructing one in a spelling the scanner "
                   + "does not anticipate — add it here only after checking the construction is owned "
                   + "and passes the join, since this check cannot verify that itself");
    }

    /// <summary>Files under <c>src/</c> permitted to name <see cref="LoopbackBrowser"/>: the class
    /// itself, <c>OAuthLoginFlow</c> which holds both construction sites, and <c>SetupJoin</c> which
    /// only mentions it in a <c>see cref</c>. Adding a fourth is a deliberate edit here, and that is
    /// the point — it is the one check no construction syntax can slip past.</summary>
    static readonly string[] BrowserNamingFiles = [
        "LoopbackBrowser.cs", "OAuthLoginFlow.cs", "SetupJoin.cs",
    ];

    // A using-alias inside a file that is ALREADY allowlisted defeats both other checks at once: the
    // alias declaration still names the type, so the file-set assertion is unchanged, while
    // `new LB(...)` names nothing the construction pattern looks for. A realistic refactor in one of
    // the two constructing flows, so the scanner resolves aliases and matches them too.
    [Test]
    public async Task Scanner_follows_a_using_alias_to_the_construction() {
        using var tmp = new TempDir();

        tmp.CreateFile("Aliased.cs", [
            "using LB = Capacitor.Cli.Core.Auth.LoopbackBrowser;",
            "namespace Fixture;",
            "static class Aliased {",
            "    static void Go() {",
            "        var browser = new LB(progress: progress);",
            "    }",
            "}",
        ]);

        await Assert.That(FindSites(tmp.Path).Count).IsEqualTo(1);
        await Assert.That(FindOwnershipViolations(tmp.Path).Count).IsEqualTo(2);
    }

    // Trivia at each junction of the alias declaration. All of these compile, and each one used to
    // defeat alias recognition — which then defeated the file-set assertion too, since the alias line
    // still names the type in a file that is already allowlisted.
    [Test]
    [Arguments("using LB = Capacitor.Cli.Core.Auth.LoopbackBrowser /* local alias */;")]
    [Arguments("using LB /* alias */ = Capacitor.Cli.Core.Auth.LoopbackBrowser;")]
    [Arguments("using /* alias */ LB = Capacitor.Cli.Core.Auth.LoopbackBrowser;")]
    [Arguments("using LB = global::Capacitor.Cli.Core.Auth.LoopbackBrowser;")]
    [Arguments("using LB = Capacitor.Cli.Core.Auth.LoopbackBrowser ;")]
    public async Task Scanner_follows_an_alias_however_the_trivia_falls(string alias) {
        using var tmp = new TempDir();

        tmp.CreateFile("AliasTrivia.cs", [
            alias,
            "namespace Fixture;",
            "static class AliasTrivia {",
            "    static void Go() {",
            "        var browser = new LB(progress: progress);",
            "    }",
            "}",
        ]);

        await Assert.That(FindSites(tmp.Path).Count).IsEqualTo(1);
        await Assert.That(FindOwnershipViolations(tmp.Path).Count).IsEqualTo(2);
    }

    // A comment is a token separator, so this compiles too — and `newLB(` must still not match.
    [Test]
    public async Task Scanner_separates_the_new_keyword_by_a_comment_but_not_by_nothing() {
        using var tmp = new TempDir();

        tmp.CreateFile("Tight.cs", [
            "namespace Fixture;",
            "static class Tight {",
            "    static void Go() {",
            "        var browser = new/* c */LoopbackBrowser(progress: progress);",
            "        var other = newLoopbackBrowser(progress);", // an unrelated method call
            "    }",
            "}",
        ]);

        await Assert.That(FindSites(tmp.Path).Count).IsEqualTo(1);
    }

    // An alias for something else must not turn every `new X(...)` in the file into a phantom site.
    [Test]
    public async Task Scanner_ignores_an_alias_for_an_unrelated_type() {
        using var tmp = new TempDir();

        tmp.CreateFile("OtherAlias.cs", [
            "using LB = System.Text.StringBuilder;",
            "namespace Fixture;",
            "static class OtherAlias {",
            "    static void Go() {",
            "        var sb = new LB();",
            "    }",
            "}",
        ]);

        await Assert.That(FindSites(tmp.Path)).IsEmpty();
    }

    // A doc comment showing the construction is documentation, not a leak.
    [Test]
    public async Task Scanner_ignores_a_construction_mentioned_in_a_comment() {
        using var tmp = new TempDir();

        tmp.CreateFile("Documented.cs", [
            "namespace Fixture;",
            "/// <summary>Callers write new LoopbackBrowser(join: ...) and dispose it.</summary>",
            "static class Documented {",
            "    // never do var b = new LoopbackBrowser();",
            "}",
        ]);

        await Assert.That(FindSites(tmp.Path)).IsEmpty();
    }

    // Paren-balancing: a nested call inside the argument list must not truncate the arguments and
    // turn a compliant site into a phantom violation.
    [Test]
    public async Task Scanner_reads_the_whole_argument_list_past_a_nested_call() {
        using var tmp = new TempDir();

        tmp.CreateFile("Nested.cs", [
            "namespace Fixture;",
            "static class Nested {",
            "    static void Go() {",
            "        using var browser = new LoopbackBrowser(openBrowser: Resolve(url), join: SetupJoin.Loopback);",
            "    }",
            "}",
        ]);

        await Assert.That(FindOwnershipViolations(tmp.Path)).IsEmpty();
    }
}
