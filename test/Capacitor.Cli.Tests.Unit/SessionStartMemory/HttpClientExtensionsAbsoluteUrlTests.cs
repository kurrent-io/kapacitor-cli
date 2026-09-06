using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.SessionStartMemory;

public class HttpClientExtensionsAbsoluteUrlTests {
    [Test]
    [Arguments("https://staging.kcap.ai/hooks/stop")]
    [Arguments("http://localhost:5108/hooks/stop")]
    [Arguments("http://127.0.0.1:5108")]
    public async Task Accepts_AbsoluteHttpAndHttps(string url) {
        await Assert.That(HttpClientExtensions.IsAcceptableUrl(url)).IsTrue();
    }

    [Test]
    [Arguments("staging.kcap.ai/hooks/stop")]
    [Arguments("/hooks/stop")]
    [Arguments("")]
    [Arguments("not a url at all")]
    public async Task Rejects_RelativeOrMalformed(string url) {
        await Assert.That(HttpClientExtensions.IsAcceptableUrl(url)).IsFalse();
    }

    [Test]
    [Arguments("file:///etc/passwd")]
    [Arguments("ftp://example.com")]
    [Arguments("javascript:alert(1)")]
    public async Task Rejects_NonHttpSchemes(string url) {
        await Assert.That(HttpClientExtensions.IsAcceptableUrl(url)).IsFalse();
    }

    /// <summary>
    /// <see cref="HookHttp.IsPostable"/> is the single predicate every hook-path guard consults.
    /// Covers all four unusable classes — whitespace, scheme-less, relative, and absolute
    /// wrong-scheme. The last is named explicitly (<c>ftp://host</c>, <c>file:///etc/passwd</c>)
    /// because an implementation validating only <c>UriKind.Absolute</c> would accept it.
    /// </summary>
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("localhost:5108")]
    [Arguments("/relative")]
    [Arguments("not a url at all")]
    [Arguments("ftp://host")]
    [Arguments("file:///etc/passwd")]
    public async Task IsPostable_rejects_every_unusable_form(string? url) {
        await Assert.That(HookHttp.IsPostable(url)).IsFalse();
    }

    [Test]
    [Arguments("http://localhost:5108")]
    [Arguments("https://kurrent.kcap.ai")]
    public async Task IsPostable_accepts_absolute_http(string url) {
        await Assert.That(HookHttp.IsPostable(url)).IsTrue();
    }
}
