namespace AngleSharp.Browser.Tests;

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Io;
using NUnit.Framework;

[TestFixture]
public class BrowserEngineTests
{
    private BrowserEngine _engine = null!;
    private string _directory = null!;

    [SetUp]
    public void Setup()
    {
        _engine = new BrowserEngine(new BrowserOptions { ViewportWidth = 800, ViewportHeight = 600 });
        _directory = Directory.CreateTempSubdirectory("anglesharp-browser-").FullName;
    }

    [TearDown]
    public void TearDown()
    {
        _engine.Dispose();
        Directory.Delete(_directory, true);
    }

    [TestCase("example.org", "https://example.org")]
    [TestCase("  https://anglesharp.github.io ", "https://anglesharp.github.io")]
    [TestCase("about:blank", "about:blank")]
    [TestCase("", "about:blank")]
    public void NormalizeAddressCompletesUserInput(string input, string expected)
    {
        Assert.That(BrowserEngine.NormalizeAddress(input), Is.EqualTo(expected));
    }

    [Test]
    public async Task PaintProducesImageOfViewportSize()
    {
        await _engine.LoadHtmlAsync("<h1>Hello</h1><p>World</p>");

        var image = await _engine.PaintAsync();

        Assert.That(image, Is.Not.Null);
        Assert.That(image!.Width, Is.EqualTo(800));
        Assert.That(image.Height, Is.EqualTo(600));
        Assert.That(image.Data[1..4], Is.EqualTo("PNG"u8.ToArray()));
    }

    [Test]
    public async Task ResizeChangesPaintedSize()
    {
        await _engine.LoadHtmlAsync("<p>Resize me</p>");

        _engine.Resize(320, 240);
        var image = await _engine.PaintAsync();

        Assert.That(image!.Width, Is.EqualTo(320));
        Assert.That(image.Height, Is.EqualTo(240));
    }

    [Test]
    public async Task NavigatingFileUpdatesTitleAndHistory()
    {
        var first = WritePage("first.html", "<title>First</title><a href='second.html'>next</a>");
        var second = WritePage("second.html", "<title>Second</title><p>done</p>");

        await _engine.NavigateAsync(first);
        Assert.That(_engine.Title, Is.EqualTo("First"));
        Assert.That(_engine.CanGoBack, Is.False);

        await _engine.NavigateAsync(second);
        Assert.That(_engine.Title, Is.EqualTo("Second"));
        Assert.That(_engine.CanGoBack, Is.True);

        await _engine.GoBackAsync();
        Assert.That(_engine.Title, Is.EqualTo("First"));
        Assert.That(_engine.CanGoForward, Is.True);

        await _engine.GoForwardAsync();
        Assert.That(_engine.Title, Is.EqualTo("Second"));
        Assert.That(_engine.CanGoForward, Is.False);
    }

    [Test]
    public async Task ClickingLinkNavigates()
    {
        var first = WritePage("first.html", "<title>First</title><p><a id=link href='second.html'>Go to the second page</a></p>");
        WritePage("second.html", "<title>Second</title><p>done</p>");
        await _engine.NavigateAsync(first);

        var (x, y) = CenterOf("link");
        await _engine.ClickAsync(x, y);

        Assert.That(_engine.Title, Is.EqualTo("Second"));
        Assert.That(_engine.CanGoBack, Is.True);
    }

    [Test]
    public async Task HoveringLinkExposesAddressAndCursor()
    {
        var page = WritePage("first.html", "<p><a id=link href='second.html'>Go to the second page</a></p>");
        await _engine.NavigateAsync(page);

        var (x, y) = CenterOf("link");
        await _engine.MovePointerAsync(x, y);

        Assert.That(_engine.HoveredLink, Does.EndWith("second.html"));
        Assert.That(_engine.Cursor, Is.EqualTo(BrowserCursor.Pointer));

        await _engine.MovePointerAsync(790, 590);

        Assert.That(_engine.HoveredLink, Is.Null);
    }

    [Test]
    public async Task ClickHandlersRunAndCanPreventNavigation()
    {
        await _engine.LoadHtmlAsync("""
            <a id=link href="https://example.invalid/">Click here please</a>
            <p id=out>before</p>
            <script>
            document.getElementById('link').addEventListener('click', function (ev) {
                ev.preventDefault();
                document.getElementById('out').textContent = 'after';
            });
            </script>
            """);

        var (x, y) = CenterOf("link");
        await _engine.ClickAsync(x, y);

        Assert.That(_engine.Document!.GetElementById("out")!.TextContent, Is.EqualTo("after"));
        Assert.That(_engine.CanGoBack, Is.False);
    }

    [Test]
    public async Task TypingIntoFocusedInputUpdatesValueAndAttribute()
    {
        await _engine.LoadHtmlAsync("<input id=name type=text>");
        var input = (IHtmlInputElement)_engine.Document!.GetElementById("name")!;

        var (x, y) = CenterOf("name");
        await _engine.ClickAsync(x, y);
        await Eventually(() => input.IsFocused);
        await _engine.TextInputAsync("Hello");
        await _engine.KeyDownAsync("Backspace");

        Assert.That(input.Value, Is.EqualTo("Hell"));
        Assert.That(input.GetAttribute("value"), Is.EqualTo("Hell"));
    }

    [Test]
    public async Task ClickingCheckboxLabelTogglesIt()
    {
        await _engine.LoadHtmlAsync("<label id=label><input id=check type=checkbox> Accept the terms</label>");
        var check = (IHtmlInputElement)_engine.Document!.GetElementById("check")!;

        var (x, y) = CenterOf("label");
        await _engine.ClickAsync(x, y);

        Assert.That(check.IsChecked, Is.True);
        Assert.That(check.HasAttribute("checked"), Is.True);
    }

    [Test]
    public async Task SubmittingFormWithEnterNavigates()
    {
        var target = WritePage("result.html", "<title>Result</title>");
        await _engine.LoadHtmlAsync($"<form action='{new Uri(target).AbsoluteUri}'><input id=q name=q></form>");
        var input = (IHtmlInputElement)_engine.Document!.GetElementById("q")!;

        var (x, y) = CenterOf("q");
        await _engine.ClickAsync(x, y);
        await Eventually(() => input.IsFocused);
        await _engine.TextInputAsync("anglesharp");
        await _engine.KeyDownAsync("Enter");

        Assert.That(_engine.Title, Is.EqualTo("Result"));
        Assert.That(_engine.Url, Does.Contain("q=anglesharp"));
    }

    [Test]
    public async Task ScrollingMovesPageWithinBounds()
    {
        await _engine.LoadHtmlAsync("<div style='height: 3000px'>tall</div>");

        await _engine.ScrollAsync(0, 500);
        Assert.That(_engine.ScrollTop, Is.EqualTo(500));

        await _engine.ScrollAsync(0, 100000);
        Assert.That(_engine.ScrollTop, Is.GreaterThan(2000).And.LessThan(3000));

        await _engine.KeyDownAsync("Home");
        Assert.That(_engine.ScrollTop, Is.EqualTo(0));
    }

    [Test]
    public async Task ScriptMutationsInvalidate()
    {
        await _engine.LoadHtmlAsync("<p id=out>before</p>");
        var invalidated = false;
        _engine.Invalidated += (_, _) => invalidated = true;

        _engine.Document!.GetElementById("out")!.TextContent = "after";

        await Eventually(() => invalidated);
    }

    [Test]
    public async Task UnreachableAddressShowsErrorPage()
    {
        await _engine.NavigateAsync("https://does-not-exist.invalid/");

        Assert.That(_engine.Document, Is.Not.Null);
        Assert.That(_engine.IsLoading, Is.False);
    }

    [Test]
    public async Task PagesWithLoadedImagesCanBePainted()
    {
        await _engine.LoadHtmlAsync("<div style='width: 20px; height: 20px; background: red'></div>");
        var png = (await _engine.PaintAsync())!.Data;
        using var server = new TestServer(new()
        {
            ["/"] = ("text/html", "<p>Image:</p><img src='/red.png' width=20 height=20>"u8.ToArray()),
            ["/red.png"] = ("image/png", png),
        });

        await _engine.NavigateAsync(server.Address);
        var img = (ILoadableElement)_engine.Document!.QuerySelector("img")!;
        await Eventually(() => img.CurrentDownload?.Task.IsCompleted == true);
        var image = await _engine.PaintAsync();

        Assert.That(image, Is.Not.Null);
    }

    [Test]
    public async Task FloatedInlineElementsCanBePaintedAndHitTested()
    {
        // older renderer versions recursed until the stack overflowed
        await _engine.LoadHtmlAsync("<ul><li style='display: inline; float: left'><a href='/a'>A</a></li><span id=s style='float: right'>B</span></ul>");

        var image = await _engine.PaintAsync();
        await _engine.MovePointerAsync(10, 10);

        Assert.That(image, Is.Not.Null);
    }

    [Test]
    public async Task RequestsReportUserAgent()
    {
        using var server = new TestServer(new()
        {
            ["/"] = ("text/html", "<p>Hi</p>"u8.ToArray()),
        });

        await _engine.NavigateAsync(server.Address);

        Assert.That(server.LastUserAgent, Is.EqualTo(_engine.Document!.DefaultView!.Navigator.UserAgent));
    }

    [Test]
    public async Task TextResponsesCanBePainted()
    {
        using var server = new TestServer(new()
        {
            ["/robots.txt"] = ("text/plain", "User-agent: *"u8.ToArray()),
        });

        await _engine.NavigateAsync(server.Address + "robots.txt");
        await Eventually(() => _engine.Document!.Body?.TextContent == "User-agent: *");
        var image = await _engine.PaintAsync();

        Assert.That(image, Is.Not.Null);
    }

    [Test]
    public async Task NoscriptIsHiddenWithScripting()
    {
        await _engine.LoadHtmlAsync("<noscript><p>Enable JavaScript</p></noscript>");

        var display = _engine.Document!.QuerySelector("noscript")!.ComputeCurrentStyle().GetPropertyValue("display");

        Assert.That(display, Is.EqualTo("none"));
    }

    [Test]
    public async Task PreparedFrameRasterizesWithOverscan()
    {
        await _engine.LoadHtmlAsync("<div style='height: 3000px'>tall</div>");

        // the first frame learns that nothing is attached to the viewport
        var first = await _engine.PrepareFrameAsync();
        var second = await _engine.PrepareFrameAsync();
        var pixels = second!.Rasterize();

        Assert.That(first!.OverscanTop, Is.EqualTo(0));
        Assert.That(second.IsScrollShiftable, Is.True);
        Assert.That(second.OverscanTop, Is.EqualTo(300));
        Assert.That(pixels.Width, Is.EqualTo(800));
        Assert.That(pixels.Height, Is.EqualTo(1200));
        Assert.That(_engine.ScrollHeight, Is.GreaterThanOrEqualTo(3000));
        Assert.That(_engine.HasPaintedDocument, Is.True);
    }

    [Test]
    public async Task FixedBoxesPreventOverscan()
    {
        await _engine.LoadHtmlAsync("<header style='position: fixed; top: 0'>Menu</header><div style='height: 3000px'>tall</div>");

        await _engine.PrepareFrameAsync();
        var frame = await _engine.PrepareFrameAsync();

        Assert.That(frame!.IsScrollShiftable, Is.False);
        Assert.That(frame.OverscanTop, Is.EqualTo(0));
        Assert.That(frame.Height, Is.EqualTo(600));
    }

    [Test]
    public async Task HoveringUsesTheLastFrameRightAway()
    {
        var page = WritePage("first.html", "<p><a id=link href='second.html'>Go to the second page</a></p><p id=text>Plain</p>");
        await _engine.NavigateAsync(page);
        await _engine.PrepareFrameAsync();
        var (x, y) = CenterOf("link");

        // answered from the frame's hit-test map, without waiting for the engine
        _engine.MovePointer(x, y);

        Assert.That(_engine.HoveredLink, Does.EndWith("second.html"));
        Assert.That(_engine.Cursor, Is.EqualTo(BrowserCursor.Pointer));

        var (textX, textY) = CenterOf("text");
        _engine.MovePointer(textX, textY);

        Assert.That(_engine.HoveredLink, Is.Null);
        Assert.That(_engine.Cursor, Is.EqualTo(BrowserCursor.Default));
        await Eventually(() => _engine.Document!.GetElementById("text")!.Matches(":hover"));
    }

    [Test]
    public async Task DocumentIsUnpaintedUntilItsFirstFrame()
    {
        await _engine.LoadHtmlAsync("<p>Hello</p>");

        Assert.That(_engine.IsLoading, Is.False);
        Assert.That(_engine.HasPaintedDocument, Is.False);

        var frame = await _engine.PrepareFrameAsync();
        frame!.Rasterize();

        Assert.That(_engine.HasPaintedDocument, Is.True);
    }

    [Test]
    public async Task NavigatingShowsTheTargetAddressRightAway()
    {
        var page = WritePage("first.html", "<p>First</p>");

        var navigation = _engine.NavigateAsync(page);

        Assert.That(_engine.IsLoading, Is.True);
        Assert.That(_engine.Url, Is.EqualTo(new Uri(page).AbsoluteUri));
        await navigation;
        Assert.That(_engine.Url, Is.EqualTo(new Uri(page).AbsoluteUri));
    }

    [Test]
    public async Task ScrollingToPositionArrivesSmoothly()
    {
        await _engine.LoadHtmlAsync("<div style='height: 3000px'>tall</div>");
        await _engine.PrepareFrameAsync();
        var positions = new System.Collections.Generic.List<double>();
        _engine.ScrollChanged += (_, _) => positions.Add(_engine.ScrollPosition);

        await _engine.ScrollToAsync(1000);

        Assert.That(_engine.ScrollTop, Is.EqualTo(1000));
        Assert.That(_engine.ScrollPosition, Is.EqualTo(1000));
        Assert.That(positions, Has.Count.GreaterThan(2));
        Assert.That(positions, Is.Ordered);
    }

    [Test]
    public async Task ScrolledFrameReusesLayout()
    {
        await _engine.LoadHtmlAsync("<div style='height: 1000px'></div><a id=link href='#'>Link</a><div style='height: 2000px'></div>");
        await _engine.PrepareFrameAsync();
        await _engine.PrepareFrameAsync();

        await _engine.ScrollToAsync(900, animate: false);
        var frame = await _engine.PrepareFrameAsync();
        _engine.MovePointer(10, 110);

        Assert.That(frame!.ScrollTop, Is.EqualTo(900));
        Assert.That(_engine.Cursor, Is.EqualTo(BrowserCursor.Pointer));
    }

    [Test]
    public async Task LinksToNewWindowsAskTheHost()
    {
        var second = WritePage("second.html", "<title>Second</title><p>Second</p>");
        var page = WritePage("first.html", $"<title>First</title><a id=link href='{new Uri(second).AbsoluteUri}' target=_blank>Go</a>");
        await _engine.NavigateAsync(page);
        string? requested = null;
        _engine.WindowRequested += (_, request) =>
        {
            requested = request.Url;
            request.Handled = true;
        };
        var (x, y) = CenterOf("link");

        await _engine.ClickAsync(x, y);

        Assert.That(requested, Is.EqualTo(new Uri(second).AbsoluteUri));
        Assert.That(_engine.Title, Is.EqualTo("First"));
    }

    [Test]
    public async Task ControlClickAsksForNewWindow()
    {
        var page = WritePage("first.html", "<a id=link href='second.html'>Go</a>");
        await _engine.NavigateAsync(page);
        string? requested = null;
        _engine.WindowRequested += (_, request) =>
        {
            requested = request.Url;
            request.Handled = true;
        };
        var (x, y) = CenterOf("link");

        await _engine.ClickAsync(x, y, InputModifiers.Control);

        Assert.That(requested, Does.EndWith("second.html"));
    }

    [Test]
    public async Task WindowsShareTheProfileButNotTheHistory()
    {
        using var other = _engine.CreateWindow();
        await _engine.LoadHtmlAsync("<p>First</p>", "https://example.com/first");
        await _engine.EvaluateScriptAsync("document.cookie = 'shared=yes'");

        await other.LoadHtmlAsync("<p>Second</p>", "https://example.com/second");
        var cookie = await other.EvaluateScriptAsync("document.cookie");

        Assert.That(cookie, Does.Contain("shared=yes"));
        Assert.That(other.CanGoBack, Is.False);
        Assert.That(_engine.Url, Is.EqualTo("https://example.com/first"));
    }

    [Test]
    public async Task WindowCanBeOpenedAfterRequestsWereMade()
    {
        using var server = new TestServer(new()
        {
            ["/"] = ("text/html", "<title>First</title>"u8.ToArray()),
            ["/second"] = ("text/html", "<title>Second</title>"u8.ToArray()),
        });
        await _engine.NavigateAsync(server.Address);

        // the HTTP handler of the opener has sent requests, so it must not be configured again
        using var other = _engine.CreateWindow();
        await other.NavigateAsync(server.Address + "second");

        Assert.That(other.Title, Is.EqualTo("Second"));
        Assert.That(_engine.Title, Is.EqualTo("First"));
    }

    [Test]
    public async Task LinksToNewWindowsOpenInPlaceWithoutHost()
    {
        var second = WritePage("second.html", "<title>Second</title><p>Second</p>");
        var page = WritePage("first.html", $"<a id=link href='{new Uri(second).AbsoluteUri}' target=_blank>Go</a>");
        await _engine.NavigateAsync(page);
        var (x, y) = CenterOf("link");

        await _engine.ClickAsync(x, y);

        Assert.That(_engine.Title, Is.EqualTo("Second"));
        Assert.That(_engine.Url, Is.EqualTo(new Uri(second).AbsoluteUri));
    }

    [Test]
    public async Task ScriptErrorsAreReported()
    {
        Exception? error = null;
        _engine.Error += (_, ex) => error = ex;

        await _engine.LoadHtmlAsync("<script>undefinedFunction()</script>");

        Assert.That(error, Is.Not.Null);
    }

    [Test]
    public async Task ScrolledFrameKeepsFixedBoxesInPlace()
    {
        await _engine.LoadHtmlAsync("<header id=bar style='position: fixed; top: 0; height: 40px; width: 100%'><a href='#'>Menu</a></header><div style='height: 3000px'></div>");
        await _engine.PrepareFrameAsync();

        await _engine.ScrollToAsync(500, animate: false);
        var frame = await _engine.PrepareFrameAsync();
        _engine.MovePointer(10, 20);

        Assert.That(frame!.ScrollTop, Is.EqualTo(500));
        Assert.That(frame.IsScrollShiftable, Is.False);
        Assert.That(_engine.Cursor, Is.EqualTo(BrowserCursor.Pointer));
    }

    private string WritePage(string name, string html)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, html);
        return path;
    }

    private (double X, double Y) CenterOf(string id)
    {
        var rect = _engine.Document!.GetElementById(id)!.GetBoundingClientRect();
        return (rect.X + Math.Min(rect.Width / 2, 20), rect.Y + rect.Height / 2);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();

        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5))
            {
                Assert.Fail("Condition was not met in time.");
            }

            await Task.Delay(20);
        }
    }
}
