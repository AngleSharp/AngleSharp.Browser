namespace AngleSharp.Browser.Tests;

using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using NUnit.Framework;

/// <summary>
/// Behavior the Acid3 test (acid3.acidtests.org) relies on.
/// </summary>
[TestFixture]
public class Acid3Tests
{
    private BrowserEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _engine = new BrowserEngine(new BrowserOptions { ViewportWidth = 800, ViewportHeight = 600 });
    }

    [TearDown]
    public void TearDown()
    {
        _engine.Dispose();
    }

    [Test]
    public async Task LinkToAPageShownInAFrameIsVisited()
    {
        using var server = new TestServer(new()
        {
            ["/"] = ("text/html", """
                <!doctype html><style>a { color: rgb(255, 0, 0) } a:visited { color: rgb(0, 128, 0) }</style>
                <a id=shown href="/frame.html">shown</a><a id=other href="/other.html">other</a>
                <iframe src="/frame.html"></iframe>
                """u8.ToArray()),
            ["/frame.html"] = ("text/html", "<!doctype html><p>frame"u8.ToArray()),
        });

        await _engine.NavigateAsync(server.Address);
        await Eventually(async () => await EvaluateAsync("document.querySelector('iframe').contentDocument.readyState") == "complete");
        // what the link's class changing does after the frame loaded in Acid3
        await _engine.EvaluateScriptAsync("document.getElementById('shown').className = 'x'; document.getElementById('other').className = 'x';");

        Assert.That(await EvaluateAsync("getComputedStyle(document.getElementById('shown')).color"), Is.EqualTo("rgba(0, 128, 0, 1)"));
        Assert.That(await EvaluateAsync("getComputedStyle(document.getElementById('other')).color"), Is.EqualTo("rgba(255, 0, 0, 1)"));
        Assert.That(await EvaluateAsync("document.querySelectorAll(':link').length"), Is.EqualTo("1"));
    }

    [Test]
    public async Task VisitsAreNotSharedWithOtherSites()
    {
        using var server = new TestServer(new()
        {
            ["/"] = ("text/html", "<!doctype html><p>visited"u8.ToArray()),
        });

        await _engine.NavigateAsync(server.Address);
        // the address of the server, linked to from another site
        await _engine.LoadHtmlAsync($"<!doctype html><a id=link href='{server.Address}'>link</a>", "https://example.org/");

        Assert.That(await EvaluateAsync("document.querySelectorAll(':visited').length"), Is.EqualTo("0"));
    }

    [Test]
    public async Task MediaQueriesOfAFrameSeeItsViewport()
    {
        await _engine.LoadHtmlAsync("<!doctype html><body style='margin:0'><iframe id=frame srcdoc='<p>x' style='border:0;width:100px;height:50px'></iframe>");

        await _engine.PrepareFrameAsync();
        await _engine.PrepareFrameAsync();

        Assert.That(await EvaluateAsync("var w = document.getElementById('frame').contentWindow; [w.innerWidth, w.innerHeight, w.matchMedia('(max-width: 100px)').matches, w.matchMedia('(min-height: 51px)').matches].join()"), Is.EqualTo("100,50,true,false"));
        Assert.That(await EvaluateAsync("matchMedia('(max-width: 100px)').matches"), Is.EqualTo("False").Or.EqualTo("false"));
    }

    [Test]
    public async Task MediaQueriesOfAFrameFollowItsSizeRightAway()
    {
        // Acid3 test 46 resizes the iframe and asks its media queries without a layout between
        await _engine.LoadHtmlAsync("<!doctype html><body style='margin:0'><iframe id=frame srcdoc='<p>x' style='border:0;width:0;height:0'></iframe>");
        await _engine.PrepareFrameAsync();

        var script = "var f = document.getElementById('frame'); var w = f.contentWindow; var before = w.matchMedia('(max-width: 1em)').matches; f.setAttribute('style', 'border:0;width:100px;height:100px'); [before, w.matchMedia('(max-width: 1em)').matches, w.matchMedia('(min-height: 1em)').matches].join()";

        Assert.That(await EvaluateAsync(script), Is.EqualTo("true,false,true"));
    }

    private async Task<string> EvaluateAsync(string script) =>
        Convert.ToString(await _engine.EvaluateScriptAsync(script), CultureInfo.InvariantCulture) ?? string.Empty;

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        var watch = Stopwatch.StartNew();

        while (!await condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5))
            {
                Assert.Fail("Condition was not met in time.");
            }

            await Task.Delay(20);
        }
    }
}
