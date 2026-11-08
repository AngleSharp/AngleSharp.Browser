namespace AngleSharp.Browser.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Dom.Events;
using AngleSharp.Io;
using AngleSharp.Js;
using AngleSharp.Renderer.Rendering;
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

    private async Task<(IDocument Nested, IHtmlInlineFrameElement Frame)> LoadFrameAsync(string frameHtml, string pageStyle = "")
    {
        var srcdoc = System.Net.WebUtility.HtmlEncode(frameHtml);
        await _engine.LoadHtmlAsync($"<!doctype html><body style=\"margin:0\"><iframe id=\"frame\" srcdoc=\"{srcdoc}\" style=\"border:0;width:400px;height:200px;margin-left:50px;margin-top:30px;display:block;{pageStyle}\"></iframe></body>");
        IHtmlInlineFrameElement frame = null!;
        await _engine.InvokeAsync(document => frame = (IHtmlInlineFrameElement)document!.GetElementById("frame")!);
        await Eventually(() => frame.ContentDocument?.ReadyState == DocumentReadyState.Complete);
        await _engine.PrepareFrameAsync();
        return (frame.ContentDocument!, frame);
    }

    [Test]
    public async Task IframeShowsItsDocumentInItsBox()
    {
        await LoadFrameAsync("<!doctype html><body style='margin:0;background:#ff0000'>");

        var frame = await _engine.PrepareFrameAsync();

        Assert.That(PixelOf(frame, 100, 100), Is.EqualTo(((byte)255, (byte)0, (byte)0)));
        Assert.That(PixelOf(frame, 10, 100), Is.EqualTo(((byte)255, (byte)255, (byte)255)));
        Assert.That(PixelOf(frame, 100, 10), Is.EqualTo(((byte)255, (byte)255, (byte)255)));
        Assert.That(PixelOf(frame, 460, 100), Is.EqualTo(((byte)255, (byte)255, (byte)255)));
    }

    [Test]
    public async Task IframeClipsWhatItsDocumentOverflowsWith()
    {
        await LoadFrameAsync("<!doctype html><body style='margin:0'><div style='width:2000px;height:2000px;background:#00ff00'></div>");

        var frame = await _engine.PrepareFrameAsync();

        Assert.That(PixelOf(frame, 100, 100), Is.EqualTo(((byte)0, (byte)255, (byte)0)));
        Assert.That(PixelOf(frame, 500, 100), Is.EqualTo(((byte)255, (byte)255, (byte)255)));
        Assert.That(PixelOf(frame, 100, 300), Is.EqualTo(((byte)255, (byte)255, (byte)255)));
    }

    [Test]
    public async Task IframeHasADefaultSize()
    {
        await _engine.LoadHtmlAsync("<!doctype html><iframe id=\"frame\" srcdoc=\"x\"></iframe>");
        var (width, height) = await _engine.InvokeAsync(document =>
        {
            var box = document!.GetElementById("frame")!.GetBoundingClientRect();
            return (box.Width, box.Height);
        });

        Assert.That(width, Is.EqualTo(304));
        Assert.That(height, Is.EqualTo(154));
    }

    [Test]
    public async Task ClickReachesTheElementInAnIframe()
    {
        var (nested, _) = await LoadFrameAsync("<!doctype html><body style='margin:0'><button id='b' style='width:100px;height:40px;margin:10px'>x</button>");
        var clicks = new List<(double X, double Y)>();
        await _engine.InvokeAsync(_ => nested.GetElementById("b")!.AddEventListener("click", (_, e) => clicks.Add((((MouseEvent)e).ClientX, ((MouseEvent)e).ClientY))));

        await _engine.ClickAsync(50 + 30, 30 + 20);

        Assert.That(clicks, Is.EqualTo(new[] { (30d, 20d) }));
    }

    [Test]
    public async Task HoveringAnElementInAnIframeMatchesHoverThere()
    {
        var (nested, _) = await LoadFrameAsync("<!doctype html><style>#b:hover{background:#ff0000}</style><body style='margin:0'><div id='b' style='width:100px;height:40px'></div>");

        await _engine.MovePointerAsync(50 + 10, 30 + 10);
        var frame = await _engine.PrepareFrameAsync();

        Assert.That(PixelOf(frame, 60, 40), Is.EqualTo(((byte)255, (byte)0, (byte)0)));
    }

    [Test]
    public async Task TypingGoesToTheFieldInAnIframe()
    {
        var (nested, _) = await LoadFrameAsync("<!doctype html><body style='margin:0'><input id='i' style='width:100px;height:30px'>");

        await _engine.ClickAsync(50 + 20, 30 + 10);
        await _engine.TextInputAsync("Hello");

        var value = await _engine.InvokeAsync(_ => ((IHtmlInputElement)nested.GetElementById("i")!).Value);
        Assert.That(value, Is.EqualTo("Hello"));
    }

    [Test]
    public async Task WheelScrollsTheIframeUnderThePointer()
    {
        var (nested, _) = await LoadFrameAsync("<!doctype html><body style='margin:0'><div style='height:2000px'></div>");

        await _engine.MovePointerAsync(50 + 10, 30 + 10);
        await _engine.ScrollAsync(0, 100);

        var (inner, page) = await _engine.InvokeAsync(_ => (nested.DocumentElement!.GetScrollTop(), _engine.ScrollTop));
        Assert.That(inner, Is.EqualTo(100));
        Assert.That(page, Is.EqualTo(0));
    }

    [Test]
    public async Task ScrollingAnIframePaintsItsDocumentMoved()
    {
        await LoadFrameAsync("<!doctype html><body style='margin:0'><div style='height:150px;background:#ff0000'></div><div style='height:2000px;background:#0000ff'></div>");

        await _engine.MovePointerAsync(50 + 10, 30 + 10);
        await _engine.ScrollAsync(0, 100);
        var frame = await _engine.PrepareFrameAsync();

        Assert.That(PixelOf(frame, 60, 30 + 10), Is.EqualTo(((byte)255, (byte)0, (byte)0)));
        Assert.That(PixelOf(frame, 60, 30 + 60), Is.EqualTo(((byte)0, (byte)0, (byte)255)));
    }

    [Test]
    public async Task WheelMovesThePageOnceTheIframeCannotScroll()
    {
        var srcdoc = "<!doctype html><body style='margin:0'>short";
        await _engine.LoadHtmlAsync($"<!doctype html><body style=\"margin:0\"><iframe id=\"frame\" srcdoc=\"{srcdoc}\" style=\"border:0;width:400px;height:200px;display:block\"></iframe><div style=\"height:3000px\"></div></body>");
        await _engine.PrepareFrameAsync();

        await _engine.MovePointerAsync(10, 10);
        await _engine.ScrollAsync(0, 100);

        await Eventually(() => _engine.ScrollPosition > 0);
    }

    [Test]
    public async Task ChangingTheDocumentOfAnIframeRepaintsThePage()
    {
        var (nested, _) = await LoadFrameAsync("<!doctype html><body style='margin:0'><div id='d' style='width:100px;height:100px;background:#ff0000'></div>");
        await _engine.PrepareFrameAsync();

        await _engine.InvokeAsync(_ => nested.GetElementById("d")!.SetAttribute("style", "width:100px;height:100px;background:#0000ff"));

        // the observer of the document in the iframe tells the page when the event loop gets to it
        var stopwatch = Stopwatch.StartNew();
        BrowserFrame frame;

        do
        {
            await Task.Delay(20);
            frame = await _engine.PrepareFrameAsync();
        }
        while (PixelOf(frame, 60, 40) != ((byte)0, (byte)0, (byte)255) && stopwatch.ElapsedMilliseconds < 5000);

        Assert.That(PixelOf(frame, 60, 40), Is.EqualTo(((byte)0, (byte)0, (byte)255)));
    }

    [Test]
    public async Task LinkInAnIframeNavigatesTheIframe()
    {
        var page = Path.Combine(_directory, "other.html");
        File.WriteAllText(page, "<!doctype html><title>other</title><p>other</p>");
        var address = new Uri(page).AbsoluteUri;
        var (_, frame) = await LoadFrameAsync($"<!doctype html><body style='margin:0'><a id='a' href='{address}' style='display:block;width:100px;height:40px'>go</a>");
        var url = _engine.Url;

        await _engine.ClickAsync(50 + 10, 30 + 10);
        await Eventually(() => frame.ContentDocument?.Title == "other");

        Assert.That(_engine.Url, Is.EqualTo(url));
    }

    [Test]
    public async Task AnIframeGoingAwayLeavesTheTasksOfThePage()
    {
        var page = Path.Combine(_directory, "other.html");
        File.WriteAllText(page, "<!doctype html><title>other</title>");
        var address = new Uri(page).AbsoluteUri;
        var (_, frame) = await LoadFrameAsync($"<!doctype html><body style='margin:0'><a href='{address}' style='display:block;width:100px;height:40px'>go</a>");
        await _engine.EvaluateScriptAsync("window.ran = 0; setTimeout(function () { window.ran = 1; }, 300);");

        await _engine.ClickAsync(50 + 10, 30 + 10);
        await Eventually(() => frame.ContentDocument?.Title == "other");
        await _engine.InvokeAsync(_ => frame.ContentDocument?.Dispose());

        var stopwatch = Stopwatch.StartNew();
        var ran = "0";

        while (ran != "1" && stopwatch.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(50);
            ran = Convert.ToString(await _engine.EvaluateScriptAsync("window.ran"), System.Globalization.CultureInfo.InvariantCulture) ?? "0";
        }

        Assert.That(ran, Is.EqualTo("1"));
    }

    [Test]
    public async Task LinkWithTargetTopInAnIframeNavigatesThePage()
    {
        var page = Path.Combine(_directory, "other.html");
        File.WriteAllText(page, "<!doctype html><title>other</title><p>other</p>");
        var address = new Uri(page).AbsoluteUri;
        await LoadFrameAsync($"<!doctype html><body style='margin:0'><a id='a' target='_top' href='{address}' style='display:block;width:100px;height:40px'>go</a>");

        await _engine.ClickAsync(50 + 10, 30 + 10);

        await Eventually(() => _engine.Title == "other");
    }

    /// <summary>
    /// Serves pages for two origins (the same server by its address and by its name).
    /// </summary>
    private sealed class TwoOriginSite : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener = new(System.Net.IPAddress.Loopback, 0);
        private readonly Dictionary<string, (string Headers, string Body)> _pages = [];

        public TwoOriginSite()
        {
            _listener.Start();
            _ = AcceptAsync();
        }

        private int Port => ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;

        public string PageOrigin => $"http://127.0.0.1:{Port}";

        public string OtherOrigin => $"http://localhost:{Port}";

        public void Add(string path, string body, string headers = "") => _pages[path] = (headers, body);

        public void Dispose() => _listener.Stop();

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    _ = ServeAsync(client);
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or System.Net.Sockets.SocketException)
            {
            }
        }

        private async Task ServeAsync(System.Net.Sockets.TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                var path = (await reader.ReadLineAsync())?.Split(' ')[1] ?? "/";

                while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
                {
                }

                var (headers, body) = _pages.GetValueOrDefault(path.Split('?')[0], (string.Empty, string.Empty));
                var content = System.Text.Encoding.UTF8.GetBytes(body);
                var head = $"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {content.Length}\r\nConnection: close\r\n{headers}\r\n";
                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(content);
            }
        }
    }

    private async Task<(IDocument Page, IHtmlInlineFrameElement Frame)> LoadTwoOriginsAsync(TwoOriginSite site, string frameAttributes, string frameAddress)
    {
        site.Add("/page", $"<!doctype html><body style=\"margin:0\"><iframe id=\"frame\" {frameAttributes} src=\"{frameAddress}\" style=\"border:0;width:400px;height:200px;display:block\"></iframe></body>");
        await _engine.NavigateAsync(site.PageOrigin + "/page");
        IDocument page = null!;
        IHtmlInlineFrameElement frame = null!;
        await _engine.InvokeAsync(document =>
        {
            page = document!;
            frame = (IHtmlInlineFrameElement)document!.GetElementById("frame")!;
        });
        return (page, frame);
    }

    private async Task<string?> EvalInAsync(IDocument document, string script) =>
        await _engine.InvokeAsync(_ => Convert.ToString(document.ExecuteScript(script), System.Globalization.CultureInfo.InvariantCulture));

    [Test]
    public async Task PageCannotReachAFrameOfAnotherOrigin()
    {
        using var site = new TwoOriginSite();
        site.Add("/frame", "<!doctype html><title>secret</title>");
        var (_, frame) = await LoadTwoOriginsAsync(site, string.Empty, site.OtherOrigin + "/frame");
        await Eventually(() => frame.ContentDocument?.ReadyState == DocumentReadyState.Complete);

        Assert.That(Convert.ToString(await _engine.EvaluateScriptAsync("String(document.getElementById('frame').contentDocument)")), Is.EqualTo("null"));
        Assert.That(Convert.ToString(await _engine.EvaluateScriptAsync("typeof document.getElementById('frame').contentWindow.document")), Is.EqualTo("undefined"));
    }

    [Test]
    public async Task FrameOfAnotherOriginCannotReachThePage()
    {
        using var site = new TwoOriginSite();
        site.Add("/frame", "<!doctype html><title>frame</title>");
        var (_, frame) = await LoadTwoOriginsAsync(site, string.Empty, site.OtherOrigin + "/frame");
        await Eventually(() => frame.ContentDocument?.ReadyState == DocumentReadyState.Complete);

        Assert.That(await EvalInAsync(frame.ContentDocument!, "typeof parent.document"), Is.EqualTo("undefined"));
        Assert.That(await EvalInAsync(frame.ContentDocument!, "typeof top.document"), Is.EqualTo("undefined"));
    }

    [Test]
    public async Task FrameOfTheSameOriginCanReachThePage()
    {
        using var site = new TwoOriginSite();
        site.Add("/frame", "<!doctype html><title>frame</title>");
        var (_, frame) = await LoadTwoOriginsAsync(site, string.Empty, site.PageOrigin + "/frame");
        await Eventually(() => frame.ContentDocument?.ReadyState == DocumentReadyState.Complete);

        Assert.That(await EvalInAsync(frame.ContentDocument!, "typeof parent.document"), Is.EqualTo("object"));
    }

    [Test]
    public async Task EngineStillSeesAndPaintsAFrameOfAnotherOrigin()
    {
        using var site = new TwoOriginSite();
        site.Add("/frame", "<!doctype html><body style='margin:0;background:#ff0000'>");
        var (_, frame) = await LoadTwoOriginsAsync(site, string.Empty, site.OtherOrigin + "/frame");
        await Eventually(() => frame.ContentDocument?.ReadyState == DocumentReadyState.Complete);

        var painted = await _engine.PrepareFrameAsync();

        Assert.That(PixelOf(painted, 100, 100), Is.EqualTo(((byte)255, (byte)0, (byte)0)));
    }

    [Test]
    public async Task MessageCrossesBetweenOrigins()
    {
        using var site = new TwoOriginSite();
        site.Add("/frame", "<!doctype html><script>window.addEventListener('message', function (e) { window.seen = e.data.text + '|' + e.origin; e.source.postMessage('back', '*'); });</script>");
        var (page, frame) = await LoadTwoOriginsAsync(site, string.Empty, site.OtherOrigin + "/frame");
        await Eventually(() => frame.ContentDocument?.ReadyState == DocumentReadyState.Complete);

        await EvalInAsync(page, "window.addEventListener('message', function (e) { window.reply = e.data + '|' + e.origin; }); document.getElementById('frame').contentWindow.postMessage({ text: 'hi' }, '*')");

        var stopwatch = Stopwatch.StartNew();
        string? reply = "undefined";

        while (reply == "undefined" && stopwatch.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(20);
            reply = await EvalInAsync(page, "String(window.reply)");
        }

        Assert.That(reply, Is.EqualTo("back|" + site.OtherOrigin));
        Assert.That(await EvalInAsync(frame.ContentDocument!, "window.seen"), Is.EqualTo("hi|" + site.PageOrigin));
    }

    [TestCase("X-Frame-Options: DENY\r\n")]
    [TestCase("X-Frame-Options: SAMEORIGIN\r\n")]
    [TestCase("Content-Security-Policy: frame-ancestors 'none'\r\n")]
    public async Task FrameRefusingToBeShownStaysEmpty(string headers)
    {
        using var site = new TwoOriginSite();
        site.Add("/frame", "<!doctype html><body style='margin:0;background:#ff0000'>", headers);
        var (_, frame) = await LoadTwoOriginsAsync(site, string.Empty, site.OtherOrigin + "/frame");
        await Task.Delay(200);

        var painted = await _engine.PrepareFrameAsync();

        Assert.That(frame.ContentDocument, Is.Null);
        Assert.That(PixelOf(painted, 100, 100), Is.EqualTo(((byte)255, (byte)255, (byte)255)));
    }

    [Test]
    public async Task SandboxedFrameRunsNoScripts()
    {
        using var site = new TwoOriginSite();
        site.Add("/frame", "<!doctype html><title>start</title><script>document.title = 'ran';</script>");
        var (_, frame) = await LoadTwoOriginsAsync(site, "sandbox", site.PageOrigin + "/frame");
        await Eventually(() => frame.ContentDocument?.ReadyState == DocumentReadyState.Complete);

        var title = await _engine.InvokeAsync(_ => frame.ContentDocument!.Title);

        Assert.That(title, Is.EqualTo("start"));
    }

    [Test]
    public async Task SandboxedFrameOfTheSameAddressIsOfAnotherOrigin()
    {
        using var site = new TwoOriginSite();
        site.Add("/frame", "<!doctype html><script>window.seen = typeof parent.document;</script>");
        var (_, frame) = await LoadTwoOriginsAsync(site, "sandbox=\"allow-scripts\"", site.PageOrigin + "/frame");
        await Eventually(() => frame.ContentDocument?.ReadyState == DocumentReadyState.Complete);

        Assert.That(await EvalInAsync(frame.ContentDocument!, "window.seen"), Is.EqualTo("undefined"));
    }

    [Test]
    public async Task SandboxedFrameCannotNavigateThePage()
    {
        using var site = new TwoOriginSite();
        site.Add("/frame", "<!doctype html><body style='margin:0'><a target='_top' href='/other' style='display:block;width:100px;height:40px'>go</a>");
        site.Add("/other", "<!doctype html><title>other</title>");
        var (page, frame) = await LoadTwoOriginsAsync(site, "sandbox", site.PageOrigin + "/frame");
        await Eventually(() => frame.ContentDocument?.ReadyState == DocumentReadyState.Complete);
        await _engine.PrepareFrameAsync();

        await _engine.ClickAsync(10, 10);
        await Task.Delay(200);

        Assert.That(_engine.Title, Is.Not.EqualTo("other"));
        Assert.That(_engine.Url, Is.EqualTo(site.PageOrigin + "/page"));
    }

    [Test]
    public async Task JavaScriptLinkInAFrameRunsInTheFrame()
    {
        using var site = new TwoOriginSite();
        site.Add("/frame", "<!doctype html><body style='margin:0'><a href='javascript:window.ranHere = 1' style='display:block;width:100px;height:40px'>go</a>");
        var (page, frame) = await LoadTwoOriginsAsync(site, string.Empty, site.OtherOrigin + "/frame");
        await Eventually(() => frame.ContentDocument?.ReadyState == DocumentReadyState.Complete);
        await _engine.PrepareFrameAsync();

        await _engine.ClickAsync(10, 10);

        Assert.That(await EvalInAsync(frame.ContentDocument!, "String(window.ranHere)"), Is.EqualTo("1"));
        Assert.That(await EvalInAsync(page, "String(window.ranHere)"), Is.EqualTo("undefined"));
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
    public async Task ClickingTheTextOfAWrappingLabelTogglesItsControl()
    {
        await _engine.LoadHtmlAsync("<label><input id=check type=checkbox> <span id=text>Accept the terms</span></label>");
        var check = (IHtmlInputElement)_engine.Document!.GetElementById("check")!;

        var (x, y) = CenterOf("text");
        await _engine.ClickAsync(x, y);

        Assert.That(check.IsChecked, Is.True);
    }

    [Test]
    public async Task ClickingALabelWithAForAttributeOnlyTogglesTheNamedControl()
    {
        await _engine.LoadHtmlAsync("<label for=missing><input id=check type=checkbox> <span id=text>Accept the terms</span></label>");
        var check = (IHtmlInputElement)_engine.Document!.GetElementById("check")!;

        var (x, y) = CenterOf("text");
        await _engine.ClickAsync(x, y);

        Assert.That(check.IsChecked, Is.False);
    }

    private const double ListBoxRowHeight = 17.8;

    private const string ListBoxStyle = "<style>body { margin: 0 } select { display: block; font: 14px/16px sans-serif; border: 0; padding: 0; width: 100px; box-sizing: border-box }</style>";

    private async Task<IHtmlSelectElement> LoadListBoxAsync(string markup)
    {
        await _engine.LoadHtmlAsync(ListBoxStyle + markup);
        await _engine.PrepareFrameAsync();
        return (IHtmlSelectElement)_engine.Document!.QuerySelector("select")!;
    }

    private Task ClickListBoxRowAsync(int row, InputModifiers modifiers = InputModifiers.None) =>
        _engine.ClickAsync(10, ((row + 0.5) * ListBoxRowHeight), modifiers);

    private static string[] SelectedTexts(IHtmlSelectElement select) =>
        select.SelectedOptions.Select(option => option.Text).ToArray();

    [Test]
    public async Task ClickingAListBoxRowSelectsOnlyIt()
    {
        var select = await LoadListBoxAsync("<select multiple size=4><option>a<option>b<option>c<option>d</select>");

        await ClickListBoxRowAsync(0);
        await ClickListBoxRowAsync(2);

        Assert.That(SelectedTexts(select), Is.EqualTo(new[] { "c" }));
        Assert.That(_engine.Document!.ActiveElement, Is.EqualTo(select));
    }

    [Test]
    public async Task ControlClickingAListBoxRowTogglesIt()
    {
        var select = await LoadListBoxAsync("<select multiple size=4><option>a<option>b<option>c<option>d</select>");

        await ClickListBoxRowAsync(0);
        await ClickListBoxRowAsync(2, InputModifiers.Control);
        Assert.That(SelectedTexts(select), Is.EqualTo(new[] { "a", "c" }));

        await ClickListBoxRowAsync(0, InputModifiers.Meta);
        Assert.That(SelectedTexts(select), Is.EqualTo(new[] { "c" }));
    }

    [Test]
    public async Task ShiftClickingAListBoxRowSelectsFromTheAnchor()
    {
        var select = await LoadListBoxAsync("<select multiple size=4><option>a<option>b<option>c<option>d</select>");

        await ClickListBoxRowAsync(1);
        await ClickListBoxRowAsync(3, InputModifiers.Shift);
        Assert.That(SelectedTexts(select), Is.EqualTo(new[] { "b", "c", "d" }));

        // the anchor stays, so the range is replaced
        await ClickListBoxRowAsync(0, InputModifiers.Shift);
        Assert.That(SelectedTexts(select), Is.EqualTo(new[] { "a", "b" }));
    }

    [Test]
    public async Task ModifiersDoNotExtendASingleSelectionListBox()
    {
        var select = await LoadListBoxAsync("<select size=3><option>a<option>b<option>c</select>");

        await ClickListBoxRowAsync(0);
        await ClickListBoxRowAsync(2, InputModifiers.Control);

        Assert.That(SelectedTexts(select), Is.EqualTo(new[] { "c" }));
    }

    [Test]
    public async Task ListBoxClicksFireEventsInTheOrderOfBrowsers()
    {
        var select = await LoadListBoxAsync("<select multiple size=2><option id=a>a<option>b</select>");
        var events = new List<string>();

        foreach (var name in new[] { "mousedown", "input", "change", "click" })
        {
            select.AddEventListener(name, (_, ev) => events.Add($"{name}@{((IElement)ev.OriginalTarget!).LocalName}"));
        }

        await ClickListBoxRowAsync(0);
        Assert.That(events, Is.EqualTo(new[] { "mousedown@option", "input@select", "change@select", "click@option" }));

        // nothing changes, so nothing is told
        events.Clear();
        await ClickListBoxRowAsync(0);
        Assert.That(events, Is.EqualTo(new[] { "mousedown@option", "click@option" }));
    }

    [Test]
    public async Task DisabledListBoxRowsCannotBeSelected()
    {
        var select = await LoadListBoxAsync("<select size=5><optgroup label=G><option>a<option disabled>b</optgroup><optgroup label=H disabled><option>c</optgroup></select>");
        var events = new List<string>();
        select.AddEventListener("click", (_, ev) => events.Add(((IElement)ev.OriginalTarget!).LocalName));

        // the label of a group takes clicks, but selects nothing
        await ClickListBoxRowAsync(0);
        Assert.That(events, Is.EqualTo(new[] { "optgroup" }));
        Assert.That(SelectedTexts(select), Is.Empty);

        events.Clear();
        await ClickListBoxRowAsync(2);
        await ClickListBoxRowAsync(4);
        Assert.That(events, Is.Empty);
        Assert.That(SelectedTexts(select), Is.Empty);

        await ClickListBoxRowAsync(1);
        Assert.That(SelectedTexts(select), Is.EqualTo(new[] { "a" }));
    }

    [Test]
    public async Task ADisabledListBoxTakesNoClicks()
    {
        var select = await LoadListBoxAsync("<select size=2 disabled><option>a<option>b</select>");

        await ClickListBoxRowAsync(0);

        Assert.That(SelectedTexts(select), Is.Empty);
        Assert.That(_engine.Document!.ActiveElement, Is.Not.EqualTo(select));
    }

    [Test]
    public async Task PressingADisabledListBoxMovesTheFocusAway()
    {
        await _engine.LoadHtmlAsync(ListBoxStyle + "<select size=2 disabled><option>a<option>b</select><input id=field>");
        await _engine.PrepareFrameAsync();
        var field = (IHtmlInputElement)_engine.Document!.GetElementById("field")!;
        var (x, y) = CenterOf("field");
        await _engine.ClickAsync(x, y);
        Assert.That(field.IsFocused, Is.True);

        await ClickListBoxRowAsync(0);

        Assert.That(field.IsFocused, Is.False);
    }

    [Test]
    public async Task ScrollingAListBoxPaintsTheRowsFurtherDown()
    {
        await LoadListBoxAsync("<select multiple size=2><option>a<option>b<option selected>c<option selected>d</select>");
        var select = _engine.Document!.QuerySelector("select")!;
        select.SetScrollTop(0);
        var top = await _engine.PrepareFrameAsync();
        var unselected = PixelOf(top!, 90, 4);

        select.SetScrollTop(2 * ListBoxRowHeight);
        var scrolled = await _engine.PrepareFrameAsync();

        Assert.That(PixelOf(scrolled!, 90, 4), Is.Not.EqualTo(unselected));
    }

    [Test]
    public async Task AListBoxScrollsToItsSelectedOption()
    {
        var select = await LoadListBoxAsync("<select size=2><option>a<option>b<option>c<option selected>d</select>");

        Assert.That(select.GetScrollTop(), Is.EqualTo(2 * ListBoxRowHeight).Within(0.5));

        // what is shown first now is "c"
        await ClickListBoxRowAsync(0);
        Assert.That(SelectedTexts(select), Is.EqualTo(new[] { "c" }));
    }

    [Test]
    public async Task ArrowKeysMoveAndExtendAListBoxSelection()
    {
        var select = await LoadListBoxAsync("<select multiple size=2><option>a<option>b<option disabled>c<option>d</select>");
        var changes = 0;
        select.AddEventListener("change", (_, _) => changes++);

        await ClickListBoxRowAsync(0);
        changes = 0;
        await _engine.KeyDownAsync("ArrowDown");
        Assert.That(SelectedTexts(select), Is.EqualTo(new[] { "b" }));
        Assert.That(changes, Is.EqualTo(1));

        // past the disabled one, and scrolled to it
        await _engine.KeyDownAsync("ArrowDown");
        Assert.That(SelectedTexts(select), Is.EqualTo(new[] { "d" }));
        Assert.That(select.GetScrollTop(), Is.EqualTo(2 * ListBoxRowHeight).Within(0.5));

        await _engine.KeyDownAsync("ArrowUp", InputModifiers.Shift);
        Assert.That(SelectedTexts(select), Is.EqualTo(new[] { "b", "d" }));
    }

    [Test]
    public async Task TheWheelScrollsAListBoxBeforeThePage()
    {
        await _engine.LoadHtmlAsync(ListBoxStyle + "<select size=2><option>a<option>b<option>c<option>d</select><div style='height: 3000px'></div>");
        await _engine.PrepareFrameAsync();
        var select = (IHtmlSelectElement)_engine.Document!.QuerySelector("select")!;
        await _engine.MovePointerAsync(10, 5);
        await _engine.PrepareFrameAsync();

        await _engine.ScrollAsync(0, ListBoxRowHeight);

        Assert.That(select.GetScrollTop(), Is.EqualTo(ListBoxRowHeight).Within(1));
        Assert.That(_engine.ScrollTop, Is.EqualTo(0));

        // nothing left to scroll: the page takes it
        await _engine.ScrollAsync(0, 500);
        await _engine.ScrollAsync(0, 40);
        Assert.That(_engine.ScrollTop, Is.GreaterThan(0));
    }

    [Test]
    public async Task ASelectedListBoxRowIsPainted()
    {
        await LoadListBoxAsync("<select size=2><option>a<option>b</select>");
        var before = await _engine.PrepareFrameAsync();
        const int X = 90;
        const int Y = 4;
        var unselected = PixelOf(before!, X, Y);

        await ClickListBoxRowAsync(0);
        var after = await _engine.PrepareFrameAsync();

        Assert.That(PixelOf(after!, X, Y), Is.Not.EqualTo(unselected));
    }

    private static (byte R, byte G, byte B) PixelOf(BrowserFrame frame, int x, int y)
    {
        var pixels = frame.Rasterize();
        var offset = (((y + frame.OverscanTop) * pixels.Width) + x) * 4;
        return (pixels.Data[offset], pixels.Data[offset + 1], pixels.Data[offset + 2]);
    }

    private const string DropdownStyle = "<style>body { margin: 0; font: 14px sans-serif } select { display: block; width: 100px; height: 20px }</style>";

    private async Task<IHtmlSelectElement> LoadDropdownAsync(string markup)
    {
        await _engine.LoadHtmlAsync(DropdownStyle + markup);
        await _engine.PrepareFrameAsync();
        return (IHtmlSelectElement)_engine.Document!.QuerySelector("select")!;
    }

    private async Task PressKeyAsync(string key, InputModifiers modifiers = InputModifiers.None)
    {
        await _engine.KeyDownAsync(key, modifiers);
        await _engine.KeyUpAsync(key, modifiers);
    }

    [Test]
    public async Task ClickingADropdownOpensItAndAgainClosesIt()
    {
        var select = await LoadDropdownAsync("<select><option>a<option>b</select>");

        await _engine.ClickAsync(10, 10);
        Assert.That(_engine.IsSelectPopupOpen, Is.True);
        Assert.That(_engine.Document!.ActiveElement, Is.EqualTo(select));

        await _engine.ClickAsync(10, 10);
        Assert.That(_engine.IsSelectPopupOpen, Is.False);
    }

    [Test]
    public async Task ClickingARowOfADropdownSelectsIt()
    {
        var select = await LoadDropdownAsync("<select><option>a<option>b<option>c</select>");
        var events = new List<string>();

        foreach (var name in new[] { "input", "change", "click" })
        {
            select.AddEventListener(name, (_, _) => events.Add(name));
        }

        await _engine.ClickAsync(10, 10);
        events.Clear();
        // the rows follow below the select: the third one
        await _engine.ClickAsync(10, 20 + 1 + (2.5 * PopupRowHeight));

        Assert.That(_engine.IsSelectPopupOpen, Is.False);
        Assert.That(select.Value, Is.EqualTo("c"));
        Assert.That(events, Is.EqualTo(new[] { "input", "change", "click" }));
    }

    // of the default font of a select: 1.2em of 13.33px and the padding of the row
    private const double PopupRowHeight = 19;

    [Test]
    public async Task DisabledRowsAndGroupsOfADropdownLeaveItOpen()
    {
        var select = await LoadDropdownAsync("<select><option>a<option disabled>b<optgroup label=G><option>c</optgroup></select>");

        await _engine.ClickAsync(10, 10);
        await _engine.ClickAsync(10, 20 + 1 + (1.5 * PopupRowHeight));
        Assert.That(_engine.IsSelectPopupOpen, Is.True);
        await _engine.ClickAsync(10, 20 + 1 + (2.5 * PopupRowHeight));
        Assert.That(_engine.IsSelectPopupOpen, Is.True);
        Assert.That(select.Value, Is.EqualTo("a"));

        await _engine.ClickAsync(10, 20 + 1 + (3.5 * PopupRowHeight));
        Assert.That(_engine.IsSelectPopupOpen, Is.False);
        Assert.That(select.Value, Is.EqualTo("c"));
    }

    [Test]
    public async Task ClickingOutsideOfADropdownClosesItAndReachesThePage()
    {
        await LoadDropdownAsync("<select><option>a<option>b</select><div id=out style='position: absolute; top: 200px; height: 50px; width: 100px'></div>");
        var clicks = 0;
        _engine.Document!.GetElementById("out")!.AddEventListener("click", (_, _) => clicks++);

        await _engine.ClickAsync(10, 10);
        await _engine.ClickAsync(10, 220);

        Assert.That(_engine.IsSelectPopupOpen, Is.False);
        Assert.That(clicks, Is.EqualTo(1));
        Assert.That(_engine.Document!.ActiveElement, Is.Not.InstanceOf<IHtmlSelectElement>());
    }

    [Test]
    public async Task ADisabledDropdownOrOnePreventedDoesNotOpen()
    {
        var select = await LoadDropdownAsync("<select disabled><option>a</select>");
        await _engine.ClickAsync(10, 10);
        Assert.That(_engine.IsSelectPopupOpen, Is.False);

        select = await LoadDropdownAsync("<select><option>a</select>");
        select.AddEventListener("mousedown", (_, ev) => ev.Cancel());
        await _engine.ClickAsync(10, 10);
        Assert.That(_engine.IsSelectPopupOpen, Is.False);

        await LoadDropdownAsync("<select></select>");
        await _engine.ClickAsync(10, 10);
        Assert.That(_engine.IsSelectPopupOpen, Is.False);
    }

    [Test]
    public async Task KeysMoveTheHighlightOfAnOpenDropdownUntilCommitted()
    {
        var select = await LoadDropdownAsync("<select><option>a0<option disabled>a1<option>a2<option selected>a3<optgroup label=G><option>g0<option disabled>g1</optgroup><optgroup label=H disabled><option>h0</optgroup><option>a4</select>");
        var changes = 0;
        select.AddEventListener("change", (_, _) => changes++);

        await _engine.ClickAsync(10, 10);
        await PressKeyAsync("ArrowDown");
        await PressKeyAsync("ArrowDown");
        Assert.That(select.Value, Is.EqualTo("a3"));
        Assert.That(changes, Is.EqualTo(0));

        // skipped what is disabled: on a4 now, then back
        await PressKeyAsync("ArrowUp");
        await PressKeyAsync("Enter");
        Assert.That(_engine.IsSelectPopupOpen, Is.False);
        Assert.That(select.Value, Is.EqualTo("g0"));
        Assert.That(changes, Is.EqualTo(1));

        // Escape takes the moved highlight back
        await PressKeyAsync("Enter");
        Assert.That(_engine.IsSelectPopupOpen, Is.True);
        await PressKeyAsync("ArrowDown");
        await PressKeyAsync("Escape");
        Assert.That(_engine.IsSelectPopupOpen, Is.False);
        Assert.That(select.Value, Is.EqualTo("g0"));

        // Tab commits and stays
        await PressKeyAsync(" ");
        await PressKeyAsync("End");
        await PressKeyAsync("Tab");
        Assert.That(select.Value, Is.EqualTo("a4"));
        Assert.That(_engine.Document!.ActiveElement, Is.EqualTo(select));

        await PressKeyAsync("ArrowDown", InputModifiers.Alt);
        Assert.That(_engine.IsSelectPopupOpen, Is.True);
        await PressKeyAsync("Escape");
    }

    [Test]
    public async Task KeysChangeAClosedDropdownRightAway()
    {
        var select = await LoadDropdownAsync("<select><option>a0<option disabled>a1<option>a2<option selected>a3<optgroup label=G><option>g0<option disabled>g1</optgroup><optgroup label=H disabled><option>h0</optgroup><option>a4</select>");
        var changes = 0;
        select.AddEventListener("change", (_, _) => changes++);
        await _engine.ClickAsync(10, 10);
        await PressKeyAsync("Escape");

        await PressKeyAsync("ArrowDown");
        Assert.That(select.Value, Is.EqualTo("g0"));
        await PressKeyAsync("ArrowDown");
        Assert.That(select.Value, Is.EqualTo("a4"));
        await PressKeyAsync("ArrowUp");
        Assert.That(select.Value, Is.EqualTo("g0"));
        await PressKeyAsync("End");
        Assert.That(select.Value, Is.EqualTo("a4"));
        await PressKeyAsync("Home");
        Assert.That(select.Value, Is.EqualTo("a0"));
        await PressKeyAsync("PageDown");
        Assert.That(select.Value, Is.EqualTo("a3"));
        Assert.That(changes, Is.EqualTo(6));

        // nowhere to go, nothing to tell
        changes = 0;
        await PressKeyAsync("End");
        await PressKeyAsync("ArrowDown");
        Assert.That(changes, Is.EqualTo(1));
    }

    [Test]
    public async Task TypingFindsTheOptionsOfADropdown()
    {
        var select = await LoadDropdownAsync("<select><option>apple<option>avocado<option disabled>banana<option>cherry<option>apricot</select>");
        await _engine.ClickAsync(10, 10);
        await PressKeyAsync("Escape");

        // goes through the ones starting with a repeated letter
        await PressKeyAsync("a");
        Assert.That(select.Value, Is.EqualTo("avocado"));
        await PressKeyAsync("a");
        Assert.That(select.Value, Is.EqualTo("apricot"));
        await Task.Delay(1100);

        // further letters narrow it down; nothing matching changes nothing
        await PressKeyAsync("c");
        Assert.That(select.Value, Is.EqualTo("cherry"));
        await PressKeyAsync("h");
        Assert.That(select.Value, Is.EqualTo("cherry"));
        await PressKeyAsync("x");
        Assert.That(select.Value, Is.EqualTo("cherry"));
        await Task.Delay(1100);

        // a disabled one isn't found
        await PressKeyAsync("b");
        Assert.That(select.Value, Is.EqualTo("cherry"));
    }

    [Test]
    public async Task TheKeysOfAnOpenDropdownAreNotForThePage()
    {
        var select = await LoadDropdownAsync("<select><option>a<option>b</select>");
        var keys = new List<string>();
        select.AddEventListener("keydown", (_, _) => keys.Add("keydown"));

        await _engine.ClickAsync(10, 10);
        await _engine.KeyDownAsync("ArrowDown");

        Assert.That(keys, Is.Empty);
    }

    [Test]
    public async Task HoveringADropdownRowHighlightsIt()
    {
        var select = await LoadDropdownAsync("<select><option>a<option>b<option>c</select>");
        await _engine.ClickAsync(10, 10);
        await _engine.PrepareFrameAsync();

        await _engine.MovePointerAsync(10, 20 + 1 + (2.5 * PopupRowHeight));
        await PressKeyAsync("Enter");

        Assert.That(select.Value, Is.EqualTo("c"));
    }

    [Test]
    public async Task AnOpenDropdownIsPaintedOnTopOfThePage()
    {
        await LoadDropdownAsync("<select><option>a<option>b</select><div style='height: 100px; background: rgb(255, 0, 0)'></div>");
        const int X = 95;
        const int Y = 30;
        var closed = PixelOf((await _engine.PrepareFrameAsync())!, X, Y);
        Assert.That(closed, Is.EqualTo(((byte)255, (byte)0, (byte)0)));

        await _engine.ClickAsync(10, 10);
        var open = PixelOf((await _engine.PrepareFrameAsync())!, X, Y);
        Assert.That(open, Is.Not.EqualTo(closed));

        await PressKeyAsync("Escape");
        Assert.That(PixelOf((await _engine.PrepareFrameAsync())!, X, Y), Is.EqualTo(closed));
    }

    [TestCase("", null, 255)]
    [TestCase("", "dark", 255)]
    [TestCase("<style>:root { color-scheme: dark }</style>", "light", 59)]
    [TestCase("<style>:root { color-scheme: light dark }</style>", "dark", 59)]
    [TestCase("<style>:root { color-scheme: light dark }</style>", "light", 255)]
    [TestCase("<style>:root { color-scheme: only dark }</style>", "light", 59)]
    [TestCase("<style>:root { color-scheme: dark } select { color-scheme: only light }</style>", "dark", 255)]
    public async Task AnOpenDropdownUsesTheColorSchemeOfThePage(string head, string? preference, int expected)
    {
        _engine.ColorScheme = preference;
        await LoadDropdownAsync(head + "<select><option>a<option>b</select>");

        await _engine.ClickAsync(10, 10);
        // the end of the first row, which isn't the highlighted one
        var frame = await _engine.PrepareFrameAsync();
        await PressKeyAsync("ArrowDown");
        frame = await _engine.PrepareFrameAsync();

        Assert.That((int)PixelOf(frame!, 90, 20 + 1 + 5).R, Is.EqualTo(expected));
        _engine.ColorScheme = null;
    }

    [Test]
    public async Task ControlsDoNotTakeTheTextColorOfThePage()
    {
        var select = await LoadDropdownAsync("<style>body { color: rgb(200, 0, 0) }</style><select><option>a</select>");

        Assert.That(select.ComputeCurrentStyle().GetPropertyValue("color"), Is.EqualTo("rgba(0, 0, 0, 1)"));
    }

    [Test]
    public async Task ClicksHitAnAbsoluteBoxWhereItIsPlacedAgainstTheViewport()
    {
        await _engine.LoadHtmlAsync("<body style='margin: 20px'><div style='margin: 50px'><button id=b style='position: absolute; left: 5px; top: 5px; width: 40px; height: 20px'>x</button></div></body>");
        await _engine.PrepareFrameAsync();
        var clicks = 0;
        _engine.Document!.GetElementById("b")!.AddEventListener("click", (_, _) => clicks++);

        await _engine.ClickAsync(10, 10);

        Assert.That(clicks, Is.EqualTo(1));
    }

    [Test]
    public async Task ClicksReachWhatEscapesAClipAndWhatIsInLaterColumns()
    {
        await _engine.LoadHtmlAsync("""
            <body style='margin: 0'>
              <div style='overflow: hidden; width: 50px; height: 20px'><button id=escaped style='position: absolute; left: 200px; top: 0; width: 40px; height: 20px'>x</button></div>
              <div style='columns: 2; column-gap: 20px; width: 400px; margin-top: 50px'>
                <div style='height: 40px; break-inside: avoid'></div>
                <button id=column style='display: block; width: 100px; height: 40px; break-inside: avoid'>y</button>
              </div>
            </body>
            """);
        await _engine.PrepareFrameAsync();
        var clicks = new List<string>();
        _engine.Document!.GetElementById("escaped")!.AddEventListener("click", (_, _) => clicks.Add("escaped"));
        _engine.Document!.GetElementById("column")!.AddEventListener("click", (_, _) => clicks.Add("column"));

        await _engine.ClickAsync(210, 10);
        var column = _engine.Document!.GetElementById("column")!.GetBoundingClientRect();
        Assert.That(column.Left, Is.EqualTo(210));
        await _engine.ClickAsync(column.Left + 10, column.Top + 10);

        Assert.That(clicks, Is.EqualTo(new[] { "escaped", "column" }));
    }

    [Test]
    public async Task ClientRectsCanBeIndexedByScripts()
    {
        await _engine.LoadHtmlAsync("<div id=box style='width: 30px; height: 10px'></div>");

        var width = await _engine.EvaluateScriptAsync("document.getElementById('box').getClientRects()[0].width");

        Assert.That(Convert.ToDouble(width), Is.EqualTo(30));
    }

    [Test]
    public async Task OpeningADropdownAsksForAFrame()
    {
        await LoadDropdownAsync("<select><option>a<option>b</select>");
        await _engine.PrepareFrameAsync();
        var invalidated = 0;
        _engine.Invalidated += (_, _) => invalidated++;

        await _engine.ClickAsync(10, 10);

        Assert.That(invalidated, Is.GreaterThan(0));
        Assert.That((await _engine.PrepareFrameAsync())!.Width, Is.GreaterThan(0));
    }

    [Test]
    public async Task ThePopupOfADropdownOpensAboveWithoutRoomBelow()
    {
        await _engine.LoadHtmlAsync(DropdownStyle + "<div style='height: 760px'></div><select><option>a<option>b<option>c<option>d</select>");
        await _engine.PrepareFrameAsync();
        var select = (IHtmlSelectElement)_engine.Document!.QuerySelector("select")!;
        var top = select.GetBoundingClientRect().Top;

        await _engine.ClickAsync(10, top + 5);
        // the first row is above the select now
        await _engine.ClickAsync(10, top - (3.5 * PopupRowHeight) - 1);

        Assert.That(select.Value, Is.EqualTo("a"));
    }

    [Test]
    public async Task ScrollingThePageClosesADropdownButTheWheelOnItsListDoesNot()
    {
        var options = string.Concat(Enumerable.Range(0, 40).Select(i => $"<option>o{i}"));
        var select = await LoadDropdownAsync($"<select>{options}</select><div style='height: 3000px'></div>");

        await _engine.ClickAsync(10, 10);
        await _engine.MovePointerAsync(10, 100);
        await _engine.ScrollAsync(0, 3 * PopupRowHeight);
        Assert.That(_engine.IsSelectPopupOpen, Is.True);
        Assert.That(_engine.ScrollTop, Is.EqualTo(0));

        // the rows moved up by three
        await _engine.ClickAsync(10, 20 + 1 + (0.5 * PopupRowHeight));
        Assert.That(select.Value, Is.EqualTo("o3"));

        await _engine.ClickAsync(10, 10);
        await _engine.MovePointerAsync(300, 700);
        await _engine.ScrollAsync(0, 40);
        Assert.That(_engine.IsSelectPopupOpen, Is.False);
    }

    [Test]
    public async Task ADropdownClosesWhenItGoesAwayLosesFocusOrTheViewportChanges()
    {
        var select = await LoadDropdownAsync("<select><option>a<option>b</select>");

        await _engine.ClickAsync(10, 10);
        await _engine.InvokeAsync(_ => select.Remove());
        await _engine.PrepareFrameAsync();
        Assert.That(_engine.IsSelectPopupOpen, Is.False);

        select = await LoadDropdownAsync("<select><option>a<option>b</select><input id=other>");
        await _engine.ClickAsync(10, 10);
        await _engine.InvokeAsync(document => ((IHtmlElement)document!.GetElementById("other")!).DoFocus());
        await _engine.PrepareFrameAsync();
        Assert.That(_engine.IsSelectPopupOpen, Is.False);

        await LoadDropdownAsync("<select><option>a<option>b</select>");
        await _engine.ClickAsync(10, 10);
        _engine.Resize(500, 500);
        await _engine.PrepareFrameAsync();
        Assert.That(_engine.IsSelectPopupOpen, Is.False);

        await LoadDropdownAsync("<select><option>a<option>b</select>");
        await _engine.ClickAsync(10, 10);
        await LoadDropdownAsync("<select><option>c</select>");
        await _engine.PrepareFrameAsync();
        Assert.That(_engine.IsSelectPopupOpen, Is.False);
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
    public async Task ScrollingWaitsForTheExtentOfAPageNotLaidOutYet()
    {
        // e.g. scripts keeping a loading page busy, so its first frame with content comes late
        await _engine.LoadHtmlAsync("<p>short</p>");
        await _engine.PrepareFrameAsync();
        await _engine.EvaluateScriptAsync("document.body.insertAdjacentHTML('beforeend', '<div style=\"height: 3000px\"></div>')");
        // the engine learns about the change from its mutation observer, a task of its own
        await _engine.EvaluateScriptAsync("0");

        var scrolling = _engine.ScrollToAsync(500, animate: false);
        Assert.That(_engine.ScrollPosition, Is.EqualTo(500));

        await _engine.PrepareFrameAsync();
        await scrolling;
        Assert.That(_engine.ScrollTop, Is.EqualTo(500));
    }

    [Test]
    public async Task FramesFollowVisibleChangesAfterHiddenOnes()
    {
        await _engine.LoadHtmlAsync("""
            <style>body { margin: 0 } #menu { position: absolute; top: 100%; display: none }</style>
            <div style="position: relative"><a id="link" href="#" style="display: block; width: 50px; height: 20px">Go</a><div id="menu">Menu</div></div>
            """);
        await _engine.PrepareFrameAsync();
        await _engine.PrepareFrameAsync();

        // shown, measured and hidden again: nothing to see
        await _engine.EvaluateScriptAsync("var m = document.getElementById('menu'); m.style.display = 'block'; m.getBoundingClientRect(); m.style.removeProperty('display')");
        await _engine.EvaluateScriptAsync("0");
        await _engine.PrepareFrameAsync();
        _engine.MovePointer(200, 10);
        Assert.That(_engine.Cursor, Is.EqualTo(BrowserCursor.Default));

        await _engine.EvaluateScriptAsync("document.getElementById('link').style.width = '300px'");
        await _engine.EvaluateScriptAsync("0");
        await _engine.PrepareFrameAsync();
        _engine.MovePointer(200, 11);

        Assert.That(_engine.Cursor, Is.EqualTo(BrowserCursor.Pointer));
    }

    [Test]
    public async Task ScrollingAScrollContainerMovesItsContent()
    {
        var items = string.Concat(Enumerable.Range(0, 10).Select(i => $"<a href='#i{i}' style='display: block; height: 30px'>Item {i}</a>"));
        await _engine.LoadHtmlAsync($"<style>body {{ margin: 0 }}</style><div style='overflow-y: auto; height: 100px'>{items}</div><div style='height: 3000px'></div>");
        await _engine.PrepareFrameAsync();
        await _engine.MovePointerAsync(5, 5);
        _engine.MovePointer(5, 5);
        Assert.That(_engine.HoveredLink, Does.EndWith("#i0"));

        await _engine.ScrollAsync(0, 60);
        await _engine.PrepareFrameAsync();
        _engine.MovePointer(5, 6);

        Assert.That(_engine.HoveredLink, Does.EndWith("#i2"));
        Assert.That(_engine.ScrollTop, Is.EqualTo(0));
    }

    [Test]
    public async Task FramesFollowChangesOfBoxesOutOfTheFlow()
    {
        await _engine.LoadHtmlAsync("""
            <style>body { margin: 0 }</style>
            <div style="height: 2000px"></div>
            <nav style="position: fixed; top: 0; left: 0"><a id="link" href="#" style="display: block; width: 50px; height: 20px">Go</a></nav>
            """);
        await _engine.PrepareFrameAsync();
        await _engine.PrepareFrameAsync();
        _engine.MovePointer(200, 10);
        Assert.That(_engine.Cursor, Is.EqualTo(BrowserCursor.Default));

        await _engine.EvaluateScriptAsync("document.getElementById('link').style.width = '300px'");
        await _engine.EvaluateScriptAsync("0");
        await _engine.PrepareFrameAsync();
        _engine.MovePointer(200, 11);

        Assert.That(_engine.Cursor, Is.EqualTo(BrowserCursor.Pointer));
    }

    [Test]
    public async Task FramesFollowChangesOfBoxesInTheFlow()
    {
        await _engine.LoadHtmlAsync("""
            <style>body { margin: 0 } #box { height: 40px }</style>
            <div id="box"><a id="link" href="#" style="display: block; width: 50px; height: 20px">Go</a></div>
            <p>After</p>
            """);
        await _engine.PrepareFrameAsync();
        await _engine.PrepareFrameAsync();
        _engine.MovePointer(200, 10);
        Assert.That(_engine.Cursor, Is.EqualTo(BrowserCursor.Default));

        await _engine.EvaluateScriptAsync("document.getElementById('link').style.width = '300px'");
        await _engine.EvaluateScriptAsync("0");
        await _engine.PrepareFrameAsync();
        _engine.MovePointer(200, 11);

        Assert.That(_engine.Cursor, Is.EqualTo(BrowserCursor.Pointer));
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
    public async Task CompressedResponsesAreDecompressedEvenIfNotAskedFor()
    {
        using var compressed = new System.IO.MemoryStream();

        using (var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write("<title>Compressed</title>"u8);
        }

        using var server = new TestServer(new()
        {
            ["/"] = ("text/html", compressed.ToArray()),
        });

        await _engine.NavigateAsync(server.Address);

        Assert.That(_engine.Document!.Title, Is.EqualTo("Compressed"));
    }

    [Test]
    public async Task TheIconOfAPageIsTheOneItsLinkDeclares()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");
        using var server = new TestServer(new()
        {
            ["/"] = ("text/html", "<html><head><link rel='shortcut icon' href='/icons/site.png'></head><body>Hi</body></html>"u8.ToArray()),
            ["/icons/site.png"] = ("image/png", png),
        });

        await _engine.NavigateAsync(server.Address);
        await Eventually(() => _engine.IconUrl is not null);
        var icon = await _engine.LoadIconAsync();

        Assert.That(_engine.IconUrl, Is.EqualTo($"{server.Address}icons/site.png"));
        Assert.That(icon, Is.EqualTo(png));
    }

    [Test]
    public async Task APageWithoutIconFallsBackToTheFaviconOfItsSite()
    {
        using var server = new TestServer(new()
        {
            ["/page"] = ("text/html", "<p>Hi</p>"u8.ToArray()),
            ["/favicon.ico"] = ("text/html", "<p>Not an image</p>"u8.ToArray()),
        });

        await _engine.NavigateAsync($"{server.Address}page");
        await Eventually(() => _engine.IconUrl is not null);

        Assert.That(_engine.IconUrl, Is.EqualTo($"{server.Address}favicon.ico"));
        // what is not an image is no icon
        Assert.That(await _engine.LoadIconAsync(), Is.Null);
    }

    [Test]
    public async Task AFaviconIsAnImageWhateverItsServerClaims()
    {
        // an icon of 1x1, as servers often send them: with a content type that is no image's
        var ico = new byte[] { 0, 0, 1, 0, 1, 0, 1, 1, 0, 0, 1, 0, 32, 0, 48, 0, 0, 0, 22, 0, 0, 0 }
            .Concat(new byte[48]).ToArray();
        using var server = new TestServer(new()
        {
            ["/"] = ("text/html", "<p>Hi</p>"u8.ToArray()),
            ["/favicon.ico"] = ("application/octet-stream", ico),
        });

        await _engine.NavigateAsync(server.Address);
        await Eventually(() => _engine.IconUrl is not null);

        Assert.That(await _engine.LoadIconAsync(), Is.EqualTo(ico));
    }

    [Test]
    public async Task IconsThatCannotBeLoadedOrShownGiveWayToTheFaviconOfTheSite()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");
        using var server = new TestServer(new()
        {
            ["/"] = ("text/html", "<html><head><link rel=icon href=/logo.svg><link rel=icon href=/missing.png><link rel=apple-touch-icon href=/touch.png></head><body>Hi</body></html>"u8.ToArray()),
            ["/logo.svg"] = ("image/svg+xml", "<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray()),
            ["/favicon.ico"] = ("image/x-icon", png),
        });

        await _engine.NavigateAsync(server.Address);
        await Eventually(() => _engine.IconUrls.Count == 4);

        Assert.That(_engine.IconUrls, Is.EqualTo(new[]
        {
            $"{server.Address}missing.png",
            $"{server.Address}touch.png",
            $"{server.Address}favicon.ico",
            $"{server.Address}logo.svg",
        }));
        Assert.That(await _engine.LoadIconAsync(), Is.EqualTo(png));
    }

    [Test]
    public async Task APageNotFromTheWebHasNoIconOrCertificate()
    {
        await _engine.LoadHtmlAsync("<p>Hi</p>");

        Assert.That(_engine.IconUrl, Is.Null);
        Assert.That(_engine.Certificate, Is.Null);
        Assert.That(await _engine.LoadIconAsync(), Is.Null);
    }

    [Test]
    public void TheCertificateOfAConnectionIsKeptWithTheVerdictOfThePlatform()
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=example.org, O=Example Inc", key, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        var names = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
        names.AddDnsName("example.org");
        names.AddDnsName("www.example.org");
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        using var handler = new System.Net.Http.HttpClientHandler();
        var certificates = new System.Collections.Concurrent.ConcurrentDictionary<string, ServerCertificate>();
        BrowserEngine.TrackCertificates(handler, certificates);

        var accepted = handler.ServerCertificateCustomValidationCallback!(
            new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, "https://example.org/"), certificate, null,
            System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors);
        var info = certificates["example.org"];

        Assert.That(accepted, Is.False);
        Assert.That(info.IsValid, Is.False);
        Assert.That(info.CommonName, Is.EqualTo("example.org"));
        Assert.That(info.Organization, Is.EqualTo("Example Inc"));
        Assert.That(info.AlternativeNames, Is.EqualTo(new[] { "example.org", "www.example.org" }));
        Assert.That(info.PublicKey, Is.EqualTo("RSA (2048 bits)"));
        Assert.That(info.Sha256Fingerprint, Has.Length.EqualTo(64));
    }

    [Test]
    public async Task RequestsCarryDefaultHeaders()
    {
        using var server = new TestServer(new()
        {
            ["/"] = ("text/html", "<p>Hi</p>"u8.ToArray()),
        });
        using var engine = new BrowserEngine(new BrowserOptions { DefaultHeaders = { ["Accept-Language"] = "de-AT", ["DNT"] = "1" } });

        await engine.NavigateAsync(server.Address);

        Assert.That(server.LastHeaders?["Accept-Language"], Is.EqualTo("de-AT"));
        Assert.That(server.LastHeaders?["DNT"], Is.EqualTo("1"));
    }

    [Test]
    public async Task DiagnosticsCollectRequestsAndConsoleOfTheCurrentPage()
    {
        using var server = new TestServer(new()
        {
            ["/"] = ("text/html", "<link rel=stylesheet href=/site.css><script>console.log('hello', 42)</script>"u8.ToArray()),
            ["/site.css"] = ("text/css", "p { color: red }"u8.ToArray()),
            ["/other"] = ("text/html", "<p>Other</p>"u8.ToArray()),
        });

        await _engine.NavigateAsync(server.Address);
        await Eventually(() => _engine.Diagnostics.Requests.Count(m => !m.IsPending) == 2);

        var requests = _engine.Diagnostics.Requests;
        Assert.That(requests.Select(m => m.Url), Is.EqualTo(new[] { server.Address, server.Address + "site.css" }));
        Assert.That(requests[1].StatusCode, Is.EqualTo(System.Net.HttpStatusCode.OK));
        Assert.That(requests[1].ContentType, Does.StartWith("text/css"));
        var message = _engine.Diagnostics.Messages.Single();
        Assert.That((message.Kind, message.Text), Is.EqualTo((DiagnosticKind.Console, "hello 42")));

        await _engine.NavigateAsync(server.Address + "other");

        Assert.That(_engine.Diagnostics.Requests.Select(m => m.Url), Is.EqualTo(new[] { server.Address + "other" }));
        Assert.That(_engine.Diagnostics.Messages, Is.Empty);
    }

    [Test]
    public async Task DiagnosticsCollectErrors()
    {
        await _engine.LoadHtmlAsync("<script>undefinedFunction()</script>");

        Assert.That(_engine.Diagnostics.Messages.Single().Kind, Is.EqualTo(DiagnosticKind.Error));
    }

    [Test]
    public async Task DiagnosticsReportTheDetailsOfScriptErrors()
    {
        await _engine.LoadHtmlAsync("""
            <script>
            function inner() {
              throw new TypeError('broken');
            }
            function outer() {
              inner();
            }
            outer();
            </script>
            """);

        var error = _engine.Diagnostics.Messages.Single();
        Assert.That(error.Text, Does.Contain("broken"));
        Assert.That(error.ErrorType, Is.EqualTo("TypeError"));
        Assert.That(error.Line, Is.EqualTo(3));
        Assert.That(error.StackTrace, Does.Contain("inner").And.Contain("outer"));
    }

    [Test]
    public async Task DiagnosticsCollectErrorsOfEventListeners()
    {
        await _engine.LoadHtmlAsync("""
            <button id=b>Go</button>
            <script>
            document.getElementById('b').addEventListener('click', function onClick() { throw new RangeError('from listener'); });
            </script>
            """);

        await _engine.EvaluateScriptAsync("document.getElementById('b').click()");
        await Eventually(() => _engine.Diagnostics.Messages.Any(m => m.Kind == DiagnosticKind.Error));

        var error = _engine.Diagnostics.Messages.Single(m => m.Kind == DiagnosticKind.Error);
        Assert.That(error.ErrorType, Is.EqualTo("RangeError"));
        Assert.That(error.StackTrace, Does.Contain("onClick"));
    }

    [Test]
    public async Task DiagnosticsCollectErrorsOfTimers()
    {
        await _engine.LoadHtmlAsync("<script>setTimeout(function later() { throw new Error('from timer'); }, 0)</script>");

        await Eventually(() => _engine.Diagnostics.Messages.Any(m => m.Kind == DiagnosticKind.Error));

        var error = _engine.Diagnostics.Messages.Single(m => m.Kind == DiagnosticKind.Error);
        Assert.That(error.ErrorType, Is.EqualTo("Error"));
        Assert.That(error.StackTrace, Does.Contain(":1:"));
    }

    [Test]
    public async Task DiagnosticsCollectTheHeadersOfRequests()
    {
        using var server = new TestServer(new()
        {
            ["/"] = ("text/html", "<p>Hello</p>"u8.ToArray()),
        });

        await _engine.NavigateAsync(server.Address);
        await Eventually(() => _engine.Diagnostics.Requests.Any(m => !m.IsPending));

        var request = _engine.Diagnostics.Requests.Single();
        Assert.That(request.RequestHeaders.Keys, Does.Contain("User-Agent"));
        Assert.That(request.ResponseHeaders["Content-Type"], Does.StartWith("text/html"));
    }

    [Test]
    public async Task DiagnosticsCollectTheTimingsOfFrames()
    {
        await _engine.LoadHtmlAsync("<p>Hello</p>");

        var frame = await _engine.PrepareFrameAsync();
        Assert.That(_engine.Diagnostics.Frames, Is.Empty);

        frame!.Rasterize();

        var timing = _engine.Diagnostics.Frames.Single();
        Assert.That(timing.Kind, Is.EqualTo(FrameKind.Layout));
        Assert.That(timing.Layout, Is.GreaterThan(TimeSpan.Zero));
        Assert.That(timing.Raster, Is.GreaterThan(TimeSpan.Zero));
        Assert.That(timing.Metrics.Style, Is.GreaterThan(TimeSpan.Zero));
        Assert.That(timing.Metrics.Layout, Is.GreaterThan(TimeSpan.Zero));
        Assert.That(timing.Metrics.Total, Is.LessThanOrEqualTo(timing.Layout));
        Assert.That(timing.Metrics.Boxes, Is.GreaterThan(0));
        Assert.That(timing.Metrics.IsIncremental, Is.False);

        _engine.Diagnostics.Clear();
        Assert.That(_engine.Diagnostics.Frames, Is.Empty);
    }

    [Test]
    public async Task DiagnosticsReportTheMetricsOfALayoutOnce()
    {
        await _engine.LoadHtmlAsync("<p>Hello</p>");
        // the first frame shows what is in view, the next one lays out all of the page
        (await _engine.PrepareFrameAsync())!.Rasterize();
        (await _engine.PrepareFrameAsync())!.Rasterize();

        // nothing changed: the frame shown is the one laid out before
        (await _engine.PrepareFrameAsync())!.Rasterize();

        var timings = _engine.Diagnostics.Frames;
        Assert.That(timings, Has.Count.EqualTo(3));
        Assert.That(timings[1].Metrics, Is.Not.SameAs(RenderMetrics.None));
        Assert.That(timings.Count(m => ReferenceEquals(m.Metrics, timings[1].Metrics)), Is.EqualTo(1));
    }

    [Test]
    public async Task APageShowsWhatIsInViewFirstAndAllOfItWithTheNextFrame()
    {
        await _engine.LoadHtmlAsync("""<body style="margin: 0"><div style="height: 5000px; background: #00ff00"></div><p>End</p>""");

        var first = await _engine.PrepareFrameAsync();
        Assert.That(PixelOf(first!, 10, 10), Is.EqualTo(((byte)0, (byte)255, (byte)0)));

        (await _engine.PrepareFrameAsync())!.Rasterize();

        var timings = _engine.Diagnostics.Frames;
        Assert.That(string.Join(", ", timings.Select(m => m.Kind)), Is.EqualTo("Layout, Layout"));
        Assert.That(timings[0].Metrics.Boxes, Is.LessThan(timings[1].Metrics.Boxes));
        Assert.That(_engine.ScrollHeight, Is.GreaterThan(5000));
    }

    [TestCase("html")]
    [TestCase("body")]
    public async Task APageWithHiddenOverflowOnItsRootIsNotScrolledByTheUser(string element)
    {
        await _engine.LoadHtmlAsync($$"""
            <style>{{element}} { overflow: hidden } body { margin: 0 }</style>
            <div style="height: 5000px">Tall</div>
            """);
        await _engine.MovePointerAsync(100, 100);
        await _engine.PrepareFrameAsync();

        await _engine.ScrollAsync(0, 300);

        Assert.That(_engine.ScrollPosition, Is.EqualTo(0));
    }

    [Test]
    public async Task ScrollContainersOfALockedPageStillScroll()
    {
        await _engine.LoadHtmlAsync("""
            <style>html, body { height: 100%; margin: 0; overflow: hidden } #box { height: 100%; overflow: auto }</style>
            <div id=box><div style="height: 5000px">Tall</div></div>
            """);
        await _engine.PrepareFrameAsync();
        await _engine.MovePointerAsync(100, 100);

        await _engine.ScrollAsync(0, 300);

        var top = await _engine.InvokeAsync(d => d!.GetElementById("box")!.GetScrollTop());
        Assert.That(top, Is.EqualTo(300));
        Assert.That(_engine.ScrollPosition, Is.EqualTo(0));
    }

    [Test]
    public async Task APageLockedWhileScrolledStaysWhereItIs()
    {
        await _engine.LoadHtmlAsync("""
            <style>body { margin: 0 } .locked { overflow: hidden }</style>
            <div style="height: 5000px">Tall</div>
            """);
        await _engine.MovePointerAsync(100, 100);
        await _engine.PrepareFrameAsync();
        await _engine.ScrollAsync(0, 300);
        Assert.That(_engine.ScrollPosition, Is.EqualTo(300));

        await _engine.EvaluateScriptAsync("document.body.className = 'locked'");
        await _engine.PrepareFrameAsync();
        await _engine.ScrollAsync(0, 300);

        Assert.That(_engine.ScrollPosition, Is.EqualTo(300));
    }

    [Test]
    public async Task InvokeAsyncWiresUpDotNetEventHandlers()
    {
        using var engine = new BrowserEngine(new BrowserOptions { ViewportWidth = 800, ViewportHeight = 600, IsScriptingEnabled = false });
        await engine.LoadHtmlAsync("<button id=apply>Apply</button><p id=out>before</p>");
        var clicks = 0;

        await engine.InvokeAsync(document => document!.GetElementById("apply")!.AddEventListener("click", (_, _) =>
        {
            clicks++;
            document.GetElementById("out")!.TextContent = "after";
        }));
        var rect = await engine.InvokeAsync(document => document!.GetElementById("apply")!.GetBoundingClientRect());
        await engine.ClickAsync(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

        Assert.That(clicks, Is.EqualTo(1));
        Assert.That(await engine.InvokeAsync(document => document!.GetElementById("out")!.TextContent), Is.EqualTo("after"));
    }

    [Test]
    public async Task ColorSchemeIsReportedToMediaQueries()
    {
        _engine.ColorScheme = "dark";
        await _engine.LoadHtmlAsync("<style>p { color: red } @media (prefers-color-scheme: dark) { p { color: blue } }</style><p id=text>Hi</p>");

        var color = await _engine.InvokeAsync(document => document!.DefaultView!.GetComputedStyle(document.GetElementById("text")!).GetColor());
        _engine.ColorScheme = "light";
        var lightColor = await _engine.InvokeAsync(document => document!.DefaultView!.GetComputedStyle(document.GetElementById("text")!).GetColor());

        Assert.That(color, Is.EqualTo("rgba(0, 0, 255, 1)"));
        Assert.That(lightColor, Is.EqualTo("rgba(255, 0, 0, 1)"));
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
        _engine.MovePointer(10, 10);

        Assert.That(frame!.ScrollTop, Is.EqualTo(500));
        Assert.That(frame.IsScrollShiftable, Is.False);
        Assert.That(_engine.Cursor, Is.EqualTo(BrowserCursor.Pointer));
    }

    [Test]
    public async Task ScrollingGoesOnWhileThePageIsBusy()
    {
        // dev.to lays out the page for each of its dropdowns on scroll, which keeps it busy for a minute
        await _engine.LoadHtmlAsync("""
            <header style='position: fixed; top: 0; height: 40px; width: 100%'>Menu</header><div style='height: 3000px'></div>
            <script>document.addEventListener('scroll', function () { var end = Date.now() + 3000; while (Date.now() < end) {} });</script>
            """);
        await _engine.PrepareFrameAsync();
        await _engine.PrepareFrameAsync();
        _ = _engine.ScrollToAsync(500, animate: false);
        await Task.Delay(500);

        var watch = Stopwatch.StartNew();
        _ = _engine.ScrollToAsync(1000, animate: false);
        var frame = await _engine.PrepareFrameAsync();

        Assert.That(frame!.ScrollTop, Is.EqualTo(1000));
        Assert.That(watch.ElapsedMilliseconds, Is.LessThan(1000));
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
