namespace AngleSharp.Browser.Tests;

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using AngleSharp.Dom;
using NUnit.Framework;

/// <summary>
/// Simple scripts a page runs: the DOM, events, timers, requests and the history.
/// </summary>
[TestFixture]
public class JavaScriptTests
{
    private const string Address = "https://example.com/";

    private BrowserEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _engine = new BrowserEngine(new BrowserOptions { ProfileDirectory = null, ViewportWidth = 800, ViewportHeight = 600 });
    }

    [TearDown]
    public void TearDown()
    {
        _engine.Dispose();
    }

    [Test]
    public async Task EvaluatingScriptsReturnsTheirValue()
    {
        await _engine.LoadHtmlAsync("<title>Scripts</title>", Address);

        Assert.That(await _engine.EvaluateScriptAsync("1 + 2"), Is.EqualTo(3.0));
        Assert.That(await _engine.EvaluateScriptAsync("'Angle' + 'Sharp'"), Is.EqualTo("AngleSharp"));
        Assert.That(await _engine.EvaluateScriptAsync("document.title"), Is.EqualTo("Scripts"));
    }

    [Test]
    public async Task EvaluatingScriptsWithoutScriptingReturnsNothing()
    {
        using var engine = new BrowserEngine(new BrowserOptions { ProfileDirectory = null, IsScriptingEnabled = false });
        await engine.LoadHtmlAsync("<title>Static</title>", Address);

        Assert.That(await engine.EvaluateScriptAsync("document.title"), Is.Null);
    }

    [Test]
    public async Task ScriptErrorsAreReportedAndThePageKeepsWorking()
    {
        await _engine.LoadHtmlAsync("<p id=out>ok</p>", Address);

        Assert.That(await CatchAsync(() => _engine.EvaluateScriptAsync("notDefined()")), Is.Not.Null);
        Assert.That(await _engine.EvaluateScriptAsync("document.getElementById('out').textContent"), Is.EqualTo("ok"));
    }

    [Test]
    public async Task ClassListToggleTakesForceIntoAccount()
    {
        await _engine.LoadHtmlAsync("<p id=out class=a>ok</p>", Address);

        var result = await _engine.EvaluateScriptAsync("""
            var list = document.getElementById('out').classList;
            [list.toggle('b', false), list.toggle('a', true), list.toggle('a'), list.toggle('a'), list.toggle('a', false), list.value].join()
            """);

        Assert.That(result, Is.EqualTo("false,true,false,true,false,"));
    }

    [Test]
    public async Task InlineScriptsBuildTheDocument()
    {
        await _engine.LoadHtmlAsync("""
            <ul id=list></ul>
            <script>
            for (var i = 1; i <= 3; i++) {
                var item = document.createElement('li');
                item.textContent = 'Item ' + i;
                document.getElementById('list').appendChild(item);
            }
            </script>
            """, Address);

        Assert.That(_engine.Document!.QuerySelectorAll("#list > li").Length, Is.EqualTo(3));
        Assert.That(_engine.Document.QuerySelector("#list > li:last-child")!.TextContent, Is.EqualTo("Item 3"));
    }

    [Test]
    public async Task TimersRunLater()
    {
        await _engine.LoadHtmlAsync("""
            <p id=out>waiting</p>
            <script>setTimeout(function () { document.getElementById('out').textContent = 'done'; }, 50);</script>
            """, Address);

        await Eventually(() => _engine.Document!.GetElementById("out")!.TextContent == "done");
    }

    [Test]
    public async Task MutationObserversIterateTheirRecords()
    {
        // dev.to loops over the records with for...of, which failed with "The value is not iterable"
        await _engine.LoadHtmlAsync("""
            <p id=out>waiting</p>
            <script>
                new MutationObserver(function (records) { for (var record of records) { document.title = record.type; } })
                    .observe(document.body, { childList: true });
                document.body.appendChild(document.createElement('div'));
            </script>
            """, Address);

        await Eventually(() => _engine.Document!.Title == "childList");
    }

    [Test]
    public async Task IntervalsWithoutDelayYieldToThePage()
    {
        // without a delay AngleSharp's interval never yields and floods the event loop
        await _engine.LoadHtmlAsync("""
            <p id=out>waiting</p>
            <script>
                var ticks = 0;
                var handle = setInterval(function () { if (++ticks === 5) { clearInterval(handle); document.getElementById('out').textContent = 'done'; } }, 0);
                setTimeout(function () { document.title = 'negative'; }, -10);
            </script>
            """, Address);

        await Eventually(() => _engine.Document!.GetElementById("out")!.TextContent == "done");
        await Eventually(() => _engine.Document!.Title == "negative");
    }

    [Test]
    public async Task ScriptedStylesChangeTheLayout()
    {
        await _engine.LoadHtmlAsync("<div id=box style='width: 100px; height: 20px'></div>", Address);

        await _engine.EvaluateScriptAsync("document.getElementById('box').style.width = '250px'");

        Assert.That(_engine.Document!.GetElementById("box")!.GetBoundingClientRect().Width, Is.EqualTo(250).Within(0.5));
    }

    [Test]
    public async Task KeyEventsCarryTheirKeyAndLegacyCodes()
    {
        await _engine.LoadHtmlAsync("""
            <input id=field>
            <script>
            window.log = [];
            ['keydown', 'keypress', 'keyup'].forEach(function (type) {
                document.getElementById('field').addEventListener(type, function (e) {
                    window.log.push(type + ':' + e.key + ':' + e.keyCode + ':' + e.which);
                });
            });
            </script>
            """, Address);
        await _engine.EvaluateScriptAsync("document.getElementById('field').focus()");

        await _engine.KeyDownAsync("Enter");
        await _engine.KeyUpAsync("Enter");
        await _engine.KeyDownAsync("a");
        await _engine.KeyUpAsync("a");

        Assert.That(await _engine.EvaluateScriptAsync("window.log.join(' ')"), Is.EqualTo(
            "keydown:Enter:13:13 keypress:Enter:13:13 keyup:Enter:13:13 keydown:a:65:65 keypress:a:97:97 keyup:a:65:65"));
    }

    [Test]
    public async Task RequestsLoadDataIntoThePage()
    {
        using var server = new TestServer(new()
        {
            ["/"] = ("text/html", """
                <ul id=list></ul>
                <script>
                var request = new XMLHttpRequest();
                request.open('GET', '/items.json');
                request.onload = function () {
                    JSON.parse(request.responseText).forEach(function (name) {
                        var item = document.createElement('li');
                        item.textContent = name;
                        document.getElementById('list').appendChild(item);
                    });
                };
                request.send();
                </script>
                """u8.ToArray()),
            ["/items.json"] = ("application/json", """["first", "second"]"""u8.ToArray()),
        });

        await _engine.NavigateAsync(server.Address);

        await Eventually(() => _engine.Document!.QuerySelectorAll("#list > li").Length == 2);
    }

    [Test]
    public async Task FetchLoadsJsonIntoThePage()
    {
        // like dev.to's base data: fetch with headers, then read the body as JSON
        using var server = new TestServer(new()
        {
            ["/"] = ("text/html", """
                <ul id=list></ul>
                <script>
                fetch('/items.json', { headers: { Accept: 'application/json' }, credentials: 'same-origin' })
                    .then(function (response) { return response.ok ? response.json() : []; })
                    .then(function (data) {
                        for (var name of data.items) {
                            var item = document.createElement('li');
                            item.textContent = name;
                            document.getElementById('list').appendChild(item);
                        }
                    });
                </script>
                """u8.ToArray()),
            ["/items.json"] = ("application/json", """{ "items": ["first", "second"] }"""u8.ToArray()),
        });

        await _engine.NavigateAsync(server.Address);

        await Eventually(() => _engine.Document!.QuerySelectorAll("#list > li").Length == 2);
    }

    [Test]
    public async Task PushStateChangesTheAddressAndGoingBackPopsTheState()
    {
        await _engine.LoadHtmlAsync("""
            <p id=out>start</p>
            <script>
            window.addEventListener('popstate', function (e) {
                document.getElementById('out').textContent = 'popped ' + (e.state === null ? 'null' : e.state);
            });
            </script>
            """, Address);

        await _engine.EvaluateScriptAsync("history.pushState('second', '', '/second')");

        Assert.That(_engine.Url, Is.EqualTo("https://example.com/second"));
        Assert.That(_engine.CanGoBack, Is.True);
        Assert.That(await _engine.EvaluateScriptAsync("history.length"), Is.EqualTo(2.0));
        Assert.That(await _engine.EvaluateScriptAsync("history.state"), Is.EqualTo("second"));

        await _engine.GoBackAsync();

        Assert.That(_engine.Url, Is.EqualTo(Address));
        Assert.That(_engine.CanGoForward, Is.True);
        Assert.That(_engine.Document!.GetElementById("out")!.TextContent, Is.EqualTo("popped null"));

        await _engine.GoForwardAsync();

        Assert.That(_engine.Url, Is.EqualTo("https://example.com/second"));
        Assert.That(_engine.Document.GetElementById("out")!.TextContent, Is.EqualTo("popped second"));
    }

    [Test]
    public async Task ScriptsCanGoBackThemselves()
    {
        await _engine.LoadHtmlAsync("<p id=out>start</p><script>window.onpopstate = function () { document.getElementById('out').textContent = 'back at ' + location.pathname; };</script>", Address);
        await _engine.EvaluateScriptAsync("history.pushState({ page: 2 }, '', 'two'); history.replaceState({ page: 3 }, '', 'three')");

        Assert.That(_engine.Url, Is.EqualTo("https://example.com/three"));

        await _engine.EvaluateScriptAsync("history.back()");

        await Eventually(() => _engine.Url == Address);
        await Eventually(() => _engine.Document!.GetElementById("out")!.TextContent.StartsWith("back at"));
    }

    [Test]
    public async Task PushStateToAnotherOriginIsRefused()
    {
        await _engine.LoadHtmlAsync("<p>same origin only</p>", Address);

        Assert.That(await CatchAsync(() => _engine.EvaluateScriptAsync("history.pushState(null, '', 'https://other.example/')")), Is.Not.Null);
        Assert.That(_engine.Url, Is.EqualTo(Address));
    }

    private static async Task<Exception?> CatchAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
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
