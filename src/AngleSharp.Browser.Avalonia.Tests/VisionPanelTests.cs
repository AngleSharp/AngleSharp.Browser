using System;
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
