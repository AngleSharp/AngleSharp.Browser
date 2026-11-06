using System;
using System.Linq;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using NUnit.Framework;
using Vision.Pages;

namespace AngleSharp.Browser.Avalonia.Tests;

/// <summary>
/// The panels looking at a page, each in its own engine like in Vision.
/// </summary>
[TestFixture]
public class VisionPanelTests
{
    private const string Page = """
        <!doctype html>
        <html><head><title>T</title>
        <style>
          h1 { color: rgb(1, 2, 3) }
          @media (min-width: 1px) { .inner { margin: 0 } }
          #gone { display: none }
        </style></head>
        <body><h1 id=title class=big>Hello</h1><div id=gone><span id=hidden>x</span></div><!-- note --></body></html>
        """;

    private BrowserEngine _target = null!;

    [SetUp]
    public void Setup() => _target = new BrowserEngine(new BrowserOptions { ProfileDirectory = null, ViewportWidth = 800, ViewportHeight = 600 });

    [TearDown]
    public void TearDown() => _target.Dispose();

    [AvaloniaTest]
    public async Task TheInspectorShowsTheCurrentDom()
    {
        await _target.LoadHtmlAsync(Page);
        using var page = new InspectorPage("light", _target);
        await page.LoadAsync();

        await WaitUntil(page, d => Tree(d).Contains("id=\"title\"") && Tree(d).Contains("note"));

        // the document is alive: what scripts or anybody else do to it shows up
        await _target.InvokeAsync(d => d!.Body!.AppendChild(d.CreateElement("article")));
        await page.Engine.InvokeAsync(d => ((IHtmlElement)d!.GetElementById("refresh")!).DoClick());

        await WaitUntil(page, d => Tree(d).Contains("article"));
    }

    [AvaloniaTest]
    public async Task SelectingAnElementShowsItsAttributesRulesAndComputedStyle()
    {
        await _target.LoadHtmlAsync(Page);
        using var page = new InspectorPage("light", _target);
        await page.LoadAsync();
        await WaitUntil(page, d => FindRow(d, "h1") is not null);

        await page.Engine.InvokeAsync(d => ((IHtmlElement)FindRow(d, "h1")!).DoClick());

        await WaitUntil(page, d => d!.GetElementById("details-top")!.TextContent.Contains("Matched rules"));
        var details = await page.Engine.InvokeAsync(d => d!.GetElementById("details-top")!.TextContent);
        var computed = await page.Engine.InvokeAsync(d => d!.GetElementById("computed")!.TextContent);
        Assert.That(details, Does.Contain("big"));
        Assert.That(details, Does.Contain("h1"));
        Assert.That(computed, Does.Contain("color"));
    }

    [AvaloniaTest]
    public async Task TheInspectorShowsTheStyleSheetsAndTheirRules()
    {
        await _target.LoadHtmlAsync(Page);
        using var page = new InspectorPage("light", _target);
        await page.LoadAsync();
        await page.Engine.InvokeAsync(d => ((IHtmlElement)d!.GetElementById("tab-cssom")!).DoClick());
        await WaitUntil(page, d => Tree(d).Contains("3 rules"));

        await page.Engine.InvokeAsync(d => ((IHtmlElement)d!.QuerySelector("#tree .twisty")!).DoClick());

        await WaitUntil(page, d => Tree(d).Contains("h1") && Tree(d).Contains("@media"));
    }

    [AvaloniaTest]
    public async Task SelectingAnyRuleShowsItsDetails()
    {
        await _target.LoadHtmlAsync("""
            <!doctype html>
            <html><head><style>
              @namespace svg url(http://www.w3.org/2000/svg);
              @font-face { font-family: X; src: url(x.woff) }
              @keyframes spin { from { opacity: 0 } to { opacity: 1 } }
              @supports (display: grid) { div { display: grid } }
              @page { margin: 1cm }
              @media print { p { color: red } }
              p::before { content: "x" }
              a:hover, b > i ~ u { color: blue }
              input::-webkit-search-cancel-button { display: none }
            </style></head><body><p>x</p></body></html>
            """);
        using var page = new InspectorPage("light", _target);
        await page.LoadAsync();
        await page.Engine.InvokeAsync(d => ((IHtmlElement)d!.GetElementById("tab-cssom")!).DoClick());
        await WaitUntil(page, d => Tree(d).Contains("rules"));

        // expand everything, then select every row once
        for (var round = 0; round < 4; round++)
        {
            var collapsed = await page.Engine.InvokeAsync(d => d!.QuerySelectorAll("#tree .twisty").Where(m => m.TextContent == "▸").Select(m => m.Closest(".row")!.GetAttribute("data-id")!).ToArray());

            foreach (var id in collapsed)
            {
                await page.Engine.InvokeAsync(d => ((IHtmlElement)d!.QuerySelector($"#tree .row[data-id='{id}'] .twisty")!).DoClick());
                await WaitUntil(page, d => d!.QuerySelector($"#tree .row[data-id='{id}'] .twisty")?.TextContent != "▸");
            }
        }

        var ids = await page.Engine.InvokeAsync(d => d!.QuerySelectorAll("#tree .row:not(.more)").Select(m => m.GetAttribute("data-id")!).ToArray());

        foreach (var id in ids)
        {
            await page.Engine.InvokeAsync(d => ((IHtmlElement)d!.QuerySelector($"#tree .row[data-id='{id}']")!).DoClick());
            await WaitUntil(page, d => d!.QuerySelector($"#tree .row.selected[data-id='{id}']") is not null);
            var (row, details) = await page.Engine.InvokeAsync(d => (d!.QuerySelector($"#tree .row[data-id='{id}']")!.TextContent, d.GetElementById("details-top")!.TextContent));
            Assert.That(details, Does.Not.Contain("failed"), row);
        }

        Assert.That(await page.Engine.InvokeAsync(Tree), Does.Contain("(unsupported selector)"));
    }

    [AvaloniaTest]
    public async Task TheRenderTreeLeavesOutWhatIsNotRendered()
    {
        await _target.LoadHtmlAsync(Page);
        using var page = new InspectorPage("light", _target);
        await page.LoadAsync();

        await page.Engine.InvokeAsync(d => ((IHtmlElement)d!.GetElementById("tab-render")!).DoClick());

        await WaitUntil(page, d => Tree(d).Contains("h1#title.big"));
        var tree = await page.Engine.InvokeAsync(Tree);
        Assert.That(tree, Does.Not.Contain("gone"));
        Assert.That(tree, Does.Not.Contain("head"));
    }

    [AvaloniaTest]
    public async Task TheDiagnosticsShowStackTraces()
    {
        await _target.LoadHtmlAsync("<script>function broken() { throw new TypeError('oh no'); } broken();</script>");
        using var page = new DiagnosticsPage("light", _target);
        await page.LoadAsync();

        await page.Engine.InvokeAsync(d => ((IHtmlElement)d!.GetElementById("tab-errors")!).DoClick());

        await WaitUntil(page, d => d!.GetElementById("errors")!.TextContent.Contains("TypeError: oh no"));
        var errors = await page.Engine.InvokeAsync(d => d!.GetElementById("errors")!.TextContent);
        Assert.That(errors, Does.Contain("broken"));
    }

    [AvaloniaTest]
    public async Task TheDiagnosticsShowTheTimingsOfFrames()
    {
        await _target.LoadHtmlAsync("<p>Hello</p>");
        (await _target.PrepareFrameAsync())!.Rasterize();
        using var page = new DiagnosticsPage("light", _target);
        await page.LoadAsync();

        await page.Engine.InvokeAsync(d => ((IHtmlElement)d!.GetElementById("tab-performance")!).DoClick());

        await WaitUntil(page, d => d!.QuerySelector("#performance .frame .layout") is not null);
    }

    [AvaloniaTest]
    public async Task TheSiteInformationTellsAboutAnInsecureConnection()
    {
        using var page = new CertificatePage("light", "http://example.org/", null, () => { });
        await page.LoadAsync();

        var text = await page.Engine.InvokeAsync(d => d!.Body!.TextContent);

        Assert.That(text, Does.Contain("example.org"));
        Assert.That(text, Does.Contain("Connection is not secure"));
    }

    [AvaloniaTest]
    public async Task TheSiteInformationShowsTheCertificateAndItsChain()
    {
        using var rootKey = System.Security.Cryptography.ECDsa.Create();
        var rootRequest = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=Test Root, O=Test Trust", rootKey, System.Security.Cryptography.HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(true, false, 0, true));
        using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddYears(5));
        using var key = System.Security.Cryptography.ECDsa.Create();
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=example.org", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
        using var leaf = request.Create(root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), [1, 2, 3, 4]);
        using var chain = new System.Security.Cryptography.X509Certificates.X509Chain();
        chain.ChainPolicy.RevocationMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck;
        chain.ChainPolicy.TrustMode = System.Security.Cryptography.X509Certificates.X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.Build(leaf);
        var certificate = ServerCertificate.Create("example.org", leaf, chain, System.Net.Security.SslPolicyErrors.None);

        using var page = new CertificatePage("light", "https://example.org/index.html", certificate, () => { });
        await page.LoadAsync();
        var text = await page.Engine.InvokeAsync(d => d!.Body!.TextContent);

        Assert.That(text, Does.Contain("Connection is secure"));
        Assert.That(text, Does.Contain("Test Trust"));
        Assert.That(text, Does.Contain("Root authority"));
        Assert.That(text, Does.Contain("01 02 03 04"));
        Assert.That(text, Does.Contain("(in 29 days)").Or.Contain("(in 30 days)"));
    }

    private static string Tree(IDocument? document) => document!.GetElementById("tree")!.TextContent;

    private static IElement? FindRow(IDocument? document, string tag)
    {
        foreach (var row in document!.QuerySelectorAll("#tree .row"))
        {
            if (row.QuerySelector(".tag")?.TextContent == tag)
            {
                return row;
            }
        }

        return null;
    }

    private static async Task WaitUntil(HtmlPage page, Func<IDocument?, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (!await page.Engine.InvokeAsync(condition))
        {
            Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "Condition was not met in time.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }
}
