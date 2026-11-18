using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using NUnit.Framework;
using Vision.Models;
using Vision.Pages;
using Vision.ViewModels;

namespace AngleSharp.Browser.Avalonia.Tests;

/// <summary>
/// The address bar of Vision: its suggestions and the browser's own pages (vision:).
/// </summary>
[TestFixture]
public class VisionAddressTests
{
    [Test]
    public void SuggestionsAreSortedByPopularityThenBySimilarity()
    {
        var history = new UrlHistory();
        Visit(history, "https://github.com/AngleSharp/AngleSharp", 1);
        Visit(history, "https://github.com/", 3);
        Visit(history, "https://gist.github.com/", 3);
        Visit(history, "https://example.org/", 9);

        var suggestions = history.Suggest("github.com").Select(m => m.Url).ToArray();

        // equally popular: the one closer to what was typed comes first
        Assert.That(suggestions, Is.EqualTo(new[] { "https://github.com/", "https://gist.github.com/", "https://github.com/AngleSharp/AngleSharp" }));
    }

    [Test]
    public void SuggestionsMatchTitlesAndLeaveOutBlankPages()
    {
        var history = new UrlHistory();
        history.Record("https://anglesharp.github.io/#docs", "AngleSharp Documentation");
        history.Record("about:blank");
        history.Record("data:text/html,documentation");

        var suggestions = history.Suggest("documentation");

        Assert.That(suggestions.Select(m => m.Url), Is.EqualTo(new[] { "https://anglesharp.github.io/" }));
        Assert.That(suggestions[0].Heading, Is.EqualTo("AngleSharp Documentation"));
        Assert.That(history.Suggest("   "), Is.Empty);
    }

    [Test]
    public void TheHistoryIsKeptInItsDirectory()
    {
        var directory = Directory.CreateTempSubdirectory("vision-history-").FullName;

        try
        {
            var history = new UrlHistory(directory);
            Visit(history, "https://example.org/", 2);
            history.SetTitle("https://example.org/", "Example");

            var loaded = new UrlHistory(directory).Suggest("example");

            Assert.That(loaded, Has.Count.EqualTo(1));
            Assert.That(loaded[0], Is.EqualTo(new UrlSuggestion("https://example.org/", "Example", 2)));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public void SimilarityGoesFromNothingToTheSame()
    {
        Assert.That(UrlHistory.GetSimilarity("github.com", "github.com"), Is.EqualTo(1));
        Assert.That(UrlHistory.GetSimilarity("GitHub.com", "github.com"), Is.EqualTo(1));
        Assert.That(UrlHistory.GetSimilarity("abc", "xyz"), Is.EqualTo(0));
        Assert.That(UrlHistory.GetSimilarity("github", "github.com"), Is.GreaterThan(UrlHistory.GetSimilarity("github", "github.com/AngleSharp")));
    }

    [AvaloniaTest]
    public async Task TheArrowKeysChooseTheSuggestionToNavigateTo()
    {
        var history = new UrlHistory();
        Visit(history, "vision:about", 2);
        Visit(history, "vision:settings", 1);
        var host = new TestHost();
        using var engine = CreateEngine(host, out var pages);
        using var viewModel = new MainViewModel(engine, "about:blank", host, history);

        viewModel.Address = "vision";

        Assert.That(viewModel.IsSuggesting, Is.True);
        Assert.That(viewModel.Suggestions.Select(m => m.Url), Is.EqualTo(new[] { "vision:about", "vision:settings" }));
        Assert.That(viewModel.SelectedSuggestionIndex, Is.EqualTo(-1));

        // down to the second one, up and around back to it
        Assert.That(viewModel.MoveSuggestion(1), Is.True);
        Assert.That(viewModel.MoveSuggestion(1), Is.True);
        Assert.That(viewModel.SelectedSuggestionIndex, Is.EqualTo(1));
        viewModel.MoveSuggestion(1);
        Assert.That(viewModel.SelectedSuggestionIndex, Is.EqualTo(-1));
        viewModel.MoveSuggestion(-1);
        Assert.That(viewModel.SelectedSuggestionIndex, Is.EqualTo(1));

        await viewModel.NavigateCommand.ExecuteAsync(null);

        Assert.That(viewModel.IsSuggesting, Is.False);
        Assert.That(viewModel.Address, Is.EqualTo("vision:settings"));
        Assert.That(engine.Url, Is.EqualTo("vision:settings"));
        pages.Dispose();
    }

    [AvaloniaTest]
    public async Task TheHistoryButtonsJumpToTheEntriesTheyList()
    {
        var host = new TestHost();
        using var engine = CreateEngine(host, out var pages);
        using var viewModel = new MainViewModel(engine, "about:blank", host);

        await engine.NavigateAsync("vision:about");
        await engine.NavigateAsync("vision:settings");
        await engine.NavigateAsync("vision:start");

        var back = viewModel.GetHistoryJumps(isBack: true);
        Assert.That(back.Select(m => (m.Delta, m.Url)), Is.EqualTo(new[] { (-1, "vision:settings"), (-2, "vision:about") }));
        Assert.That(back.All(m => m.Heading.Length > 0), Is.True);
        Assert.That(viewModel.GetHistoryJumps(isBack: false), Is.Empty);

        await viewModel.JumpAsync(back[1]);

        Assert.That(engine.Url, Is.EqualTo("vision:about"));
        Assert.That(viewModel.GetHistoryJumps(isBack: false).Select(m => (m.Delta, m.Url)),
            Is.EqualTo(new[] { (1, "vision:settings"), (2, "vision:start") }));
        pages.Dispose();
    }

    [AvaloniaTest]
    public async Task WithoutAChosenSuggestionTheTypedAddressIsUsed()
    {
        var history = new UrlHistory();
        Visit(history, "vision:about", 1);
        var host = new TestHost();
        using var engine = CreateEngine(host, out var pages);
        using var viewModel = new MainViewModel(engine, "about:blank", host, history);

        viewModel.Address = "vision:pages";
        Assert.That(viewModel.IsSuggesting, Is.False);
        viewModel.Address = "vision:";
        Assert.That(viewModel.IsSuggesting, Is.True);
        Assert.That(viewModel.CloseSuggestions(), Is.True);
        Assert.That(viewModel.MoveSuggestion(1), Is.False);

        await viewModel.NavigateCommand.ExecuteAsync(null);

        Assert.That(engine.Url, Is.EqualTo("vision:"));
        pages.Dispose();
    }

    [AvaloniaTest]
    public async Task TheSettingsAreAVisionPage()
    {
        var host = new TestHost();
        using var engine = CreateEngine(host, out var pages);

        await engine.NavigateAsync("vision:settings");

        await WaitUntil(engine, d => d?.GetElementById("start-page") is IHtmlInputElement { Value: BrowserSettings.DefaultStartPage });
        Assert.That(engine.Title, Is.EqualTo("Settings"));

        // the page is wired up: the button works
        await engine.InvokeAsync(d => ((IHtmlElement)d!.GetElementById("reset")!).DoClick());
        await WaitUntil(engine, d => d!.GetElementById("status")!.TextContent.Contains("Defaults restored"));
        pages.Dispose();
    }

    [AvaloniaTest]
    public async Task AnUnknownVisionPageListsThePagesThereAre()
    {
        var host = new TestHost();
        using var engine = CreateEngine(host, out var pages);

        await engine.NavigateAsync("vision:nothing");

        var (notice, links) = await engine.InvokeAsync(d => (
            d!.GetElementById("notice")?.TextContent,
            d.QuerySelectorAll("#pages a").Select(m => m.GetAttribute("href")).ToArray()));
        Assert.That(notice, Does.Contain("vision:nothing"));
        Assert.That(links, Is.EqualTo(VisionPages.Available.Select(m => m.Address).ToArray()));

        await engine.InvokeAsync(d => ((IHtmlElement)d!.QuerySelector("#pages a[href='vision:about']")!).DoClick());
        await WaitUntil(engine, d => d?.Url == "vision:about" && d.GetElementById("version")!.TextContent.StartsWith("Version"));

        await engine.NavigateAsync("vision:pages");
        Assert.That(await engine.InvokeAsync(d => d!.GetElementById("notice")), Is.Null);
        pages.Dispose();
    }

    [AvaloniaTest]
    public async Task TheStartPageShowsTheMostVisitedPagesDemosAndVisionPages()
    {
        var history = new UrlHistory();
        Visit(history, "vision:settings", 9);

        for (var i = 1; i <= 7; i++)
        {
            Visit(history, $"https://example.org/{i}", i);
        }

        history.SetTitle("https://example.org/7", "Seven");
        var host = new TestHost();
        using var engine = CreateEngine(host, out var pages, history);

        await engine.NavigateAsync("vision:start");

        var (visited, demos, vision, heading) = await engine.InvokeAsync(d => (
            d!.QuerySelectorAll("#most-visited a").Select(m => m.GetAttribute("href")).ToArray(),
            d.QuerySelectorAll("#demos a").Select(m => m.GetAttribute("href")).ToArray(),
            d.QuerySelectorAll("#pages a").Select(m => m.GetAttribute("href")).ToArray(),
            d.QuerySelector("#most-visited .name")!.TextContent));
        // the pages of Vision have their own section
        Assert.That(visited, Is.EqualTo(new[] { "https://example.org/7", "https://example.org/6", "https://example.org/5", "https://example.org/4", "https://example.org/3" }));
        Assert.That(heading, Is.EqualTo("Seven"));
        Assert.That(demos, Has.Length.EqualTo(5));
        Assert.That(demos, Does.Contain("https://news.ycombinator.com"));
        Assert.That(vision, Is.EqualTo(new[] { "vision:requester", "vision:settings", "vision:about" }));
        Assert.That(BrowserSettings.DefaultStartPage, Is.EqualTo("vision:start"));
        pages.Dispose();
    }

    [AvaloniaTest]
    public async Task TheStartPageWithoutAHistoryTellsWhatWillBeThere()
    {
        var host = new TestHost();
        using var engine = CreateEngine(host, out var pages);

        await engine.NavigateAsync("vision:start");

        var empty = await engine.InvokeAsync(d => d!.QuerySelector("#most-visited .empty")?.TextContent);
        Assert.That(empty, Does.Contain("visit most"));
        pages.Dispose();
    }

    [AvaloniaTest]
    public async Task TheRequesterSendsWithTheRequestersOfTheEngine()
    {
        var host = new TestHost();
        using var engine = CreateEngine(host, out var pages);

        await engine.NavigateAsync("vision:requester");
        await WaitUntil(engine, d => d?.QuerySelectorAll("#requester option").Length > 1);

        // the browser's own pages come from a requester as well
        await engine.InvokeAsync(d =>
        {
            ((IHtmlInputElement)d!.GetElementById("url")!).Value = "vision:about";
            ((IHtmlElement)d.GetElementById("send")!).DoClick();
        });
        await WaitUntil(engine, d => d!.GetElementById("response-status")!.TextContent == "200 OK");

        var (body, headers, requester) = await engine.InvokeAsync(d => (
            d!.GetElementById("body-text")!.TextContent,
            d.GetElementById("headers-list")!.TextContent,
            d.GetElementById("response-requester")!.TextContent));
        Assert.That(body, Does.Contain("<title>About"));
        Assert.That(headers, Does.Contain("text/html"));
        Assert.That(requester, Is.EqualTo("Scheme"));
        pages.Dispose();
    }

    [AvaloniaTest]
    public async Task TheRequesterPostsABodyWithTheDefaultHeaders()
    {
        using var listener = new HttpListener();
        var port = GetFreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var received = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            using var reader = new StreamReader(context.Request.InputStream);
            var request = (context.Request.HttpMethod, Body: await reader.ReadToEndAsync(), Type: context.Request.ContentType, Test: context.Request.Headers["X-Test"]);
            context.Response.ContentType = "application/json";
            var answer = System.Text.Encoding.UTF8.GetBytes("{\"ok\":true}");
            await context.Response.OutputStream.WriteAsync(answer);
            context.Response.Close();
            return request;
        });
        var host = new TestHost();
        var options = new BrowserOptions { ProfileDirectory = null, ViewportWidth = 800, ViewportHeight = 600 };
        options.DefaultHeaders["X-Test"] = "default";
        using var engine = CreateEngine(host, out var pages, options: options);

        await engine.NavigateAsync("vision:requester");
        await WaitUntil(engine, d => d?.QuerySelectorAll("#requester option").Length > 1);
        await engine.InvokeAsync(d =>
        {
            ((IHtmlSelectElement)d!.GetElementById("method")!).Value = "Post";
            ((IHtmlInputElement)d.GetElementById("url")!).Value = $"http://127.0.0.1:{port}/echo";
            ((IHtmlTextAreaElement)d.GetElementById("body")!).Value = "{\"name\":\"Vision\"}";
            ((IHtmlElement)d.GetElementById("send")!).DoClick();
        });
        await WaitUntil(engine, d => d!.GetElementById("response-status")!.TextContent == "200 OK");

        var request = await received;
        Assert.That(request.HttpMethod, Is.EqualTo("POST"));
        Assert.That(request.Body, Is.EqualTo("{\"name\":\"Vision\"}"));
        Assert.That(request.Type, Does.StartWith("application/json"));
        Assert.That(request.Test, Is.EqualTo("default"));
        // JSON is shown indented, and what was sent includes the default headers
        var (body, sent) = await engine.InvokeAsync(d => (
            d!.GetElementById("body-text")!.TextContent,
            d.GetElementById("request-headers")!.TextContent));
        Assert.That(body, Does.Contain("\"ok\": true"));
        Assert.That(sent, Does.Contain("X-Test"));
        pages.Dispose();
    }

    [AvaloniaTest]
    public async Task TheRequesterTellsWhatIsWrongWithARequest()
    {
        var host = new TestHost();
        using var engine = CreateEngine(host, out var pages);

        await engine.NavigateAsync("vision:requester");
        await WaitUntil(engine, d => d?.QuerySelectorAll("#requester option").Length > 1);
        await engine.InvokeAsync(d =>
        {
            ((IHtmlTextAreaElement)d!.GetElementById("body")!).Value = "text";
            ((IHtmlElement)d.GetElementById("send")!).DoClick();
        });

        var status = await engine.InvokeAsync(d => d!.GetElementById("status")!.TextContent);
        Assert.That(status, Does.Contain("GET request has no body"));
        pages.Dispose();
    }

    [AvaloniaTest]
    public async Task TheRequesterSavesRestoresAndDeletesRequests()
    {
        var host = new TestHost();
        var saved = new SavedRequests();
        using var engine = CreateEngine(host, out var pages, requests: saved);

        await engine.NavigateAsync("vision:requester");
        await WaitUntil(engine, d => d?.QuerySelectorAll("#requester option").Length > 1);
        Assert.That(await engine.InvokeAsync(d => d!.QuerySelector("#saved .none")), Is.Not.Null);

        // the button below the response asks for a name, the method and address by default
        await engine.InvokeAsync(d =>
        {
            ((IHtmlSelectElement)d!.GetElementById("method")!).Value = "Post";
            ((IHtmlInputElement)d.GetElementById("url")!).Value = "https://example.org/api/items";
            ((IHtmlTextAreaElement)d.GetElementById("body")!).Value = "{}";
            ((IHtmlElement)d.GetElementById("save")!).DoClick();
        });
        // the click is dispatched after this
        await WaitUntil(engine, d => d!.GetElementById("modal")!.ClassList.Contains("open"));
        Assert.That(await engine.InvokeAsync(d => ((IHtmlInputElement)d!.GetElementById("name")!).Value), Is.EqualTo("POST example.org/api/items"));
        await engine.InvokeAsync(d => ((IHtmlElement)d!.GetElementById("save-confirm")!).DoClick());
        await WaitUntil(engine, d => d!.QuerySelectorAll("#saved .item").Length == 1 && !d.GetElementById("modal")!.ClassList.Contains("open"));
        Assert.That(await engine.InvokeAsync(d => d!.GetElementById("menu-count")!.TextContent), Is.EqualTo("1"));

        // cancelling saves nothing
        await engine.InvokeAsync(d => ((IHtmlElement)d!.GetElementById("save")!).DoClick());
        await WaitUntil(engine, d => d!.GetElementById("modal")!.ClassList.Contains("open"));
        await engine.InvokeAsync(d => ((IHtmlElement)d!.GetElementById("save-cancel")!).DoClick());
        await WaitUntil(engine, d => !d!.GetElementById("modal")!.ClassList.Contains("open"));

        await engine.InvokeAsync(d => ((IHtmlElement)d!.GetElementById("save")!).DoClick());
        await WaitUntil(engine, d => d!.GetElementById("modal")!.ClassList.Contains("open"));
        await engine.InvokeAsync(d =>
        {
            ((IHtmlInputElement)d!.GetElementById("name")!).Value = "Items";
            ((IHtmlElement)d.GetElementById("save-confirm")!).DoClick();
        });
        await WaitUntil(engine, d => d!.QuerySelectorAll("#saved .item").Length == 2);
        Assert.That(saved.All.Select(m => m.Name), Is.EqualTo(new[] { "Items", "POST example.org/api/items" }));
        Assert.That(saved.Find("items")!.Body, Is.EqualTo("{}"));

        // the saved requests are in the sheet the menu button opens
        await engine.InvokeAsync(d => ((IHtmlElement)d!.GetElementById("menu")!).DoClick());
        await WaitUntil(engine, d => d!.GetElementById("sheet")!.ClassList.Contains("open"));

        // what the form shows is replaced by the saved request, which closes the sheet
        await engine.InvokeAsync(d =>
        {
            ((IHtmlSelectElement)d!.GetElementById("method")!).Value = "Get";
            ((IHtmlInputElement)d.GetElementById("url")!).Value = "https://other.org/";
            ((IHtmlTextAreaElement)d.GetElementById("body")!).Value = string.Empty;
            ((IHtmlElement)d.QuerySelector("#saved .item[data-name='Items'] .name")!).DoClick();
        });
        await WaitUntil(engine, d => d!.QuerySelector("#saved .item.active") is not null);
        var (method, url, body, active, methodClass) = await engine.InvokeAsync(d => (
            ((IHtmlSelectElement)d!.GetElementById("method")!).Value,
            ((IHtmlInputElement)d.GetElementById("url")!).Value,
            ((IHtmlTextAreaElement)d.GetElementById("body")!).Value,
            d.QuerySelector("#saved .item.active")?.GetAttribute("data-name"),
            d.GetElementById("method")!.ClassName));
        Assert.That((method, url, body, active), Is.EqualTo(("Post", "https://example.org/api/items", "{}", "Items")));
        Assert.That(methodClass, Does.Contain("post"));
        Assert.That(await engine.InvokeAsync(d => d!.GetElementById("sheet")!.ClassList.Contains("open")), Is.False);

        await engine.InvokeAsync(d => ((IHtmlElement)d!.QuerySelector("#saved .item[data-name='Items'] .remove")!).DoClick());
        await WaitUntil(engine, d => d!.QuerySelectorAll("#saved .item").Length == 1);
        Assert.That(saved.Find("Items"), Is.Null);
        pages.Dispose();
    }

    [Test]
    public void SavedRequestsAreKeptInTheirDirectory()
    {
        var directory = Directory.CreateTempSubdirectory("vision-requests-").FullName;

        try
        {
            var requests = new SavedRequests(directory);
            requests.Save(new SavedRequest("First", "Get", "https://example.org/", "Accept: */*", string.Empty, null, true));
            requests.Save(new SavedRequest("Second", "Post", "https://example.org/", string.Empty, "x", "HttpClient", false));
            requests.Save(new SavedRequest("first", "Delete", "https://example.org/1", string.Empty, string.Empty, null, true));

            var loaded = new SavedRequests(directory).All;

            // saving a name again replaces the request, the most recent first
            Assert.That(loaded.Select(m => (m.Name, m.Method)), Is.EqualTo(new[] { ("first", "Delete"), ("Second", "Post") }));
            Assert.That(loaded[1].Requester, Is.EqualTo("HttpClient"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestCase("vision:settings", "settings")]
    [TestCase("vision://About/", "about")]
    [TestCase("vision:settings?x=1#y", "settings")]
    [TestCase("vision:", "")]
    public void TheNameOfAVisionPageComesFromItsAddress(string url, string name)
    {
        Assert.That(VisionPages.GetName(url), Is.EqualTo(name));
    }

    private static void Visit(UrlHistory history, string url, int times)
    {
        for (var i = 0; i < times; i++)
        {
            history.Record(url);
        }
    }

    private static BrowserEngine CreateEngine(IBrowserHost host, out VisionPages pages, UrlHistory? history = null, BrowserOptions? options = null, SavedRequests? requests = null)
    {
        options ??= new BrowserOptions { ProfileDirectory = null, ViewportWidth = 800, ViewportHeight = 600 };
        pages = new VisionPages(host, history, requests);
        options.SchemeHandlers[VisionPages.Scheme] = pages.HandleAsync;
        var engine = new BrowserEngine(options);
        pages.Attach(engine);
        return engine;
    }

    private static int GetFreePort()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    private static async Task WaitUntil(BrowserEngine engine, Func<IDocument?, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (!await engine.InvokeAsync(condition))
        {
            Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "Condition was not met in time.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    private sealed class TestHost : IBrowserHost
    {
        // like the application, whose theme is an Avalonia property
        public string ColorScheme
        {
            get
            {
                Dispatcher.UIThread.VerifyAccess();
                return "light";
            }
        }

        public string StartPage => "about:blank";

        public event EventHandler? ColorSchemeChanged
        {
            add { }
            remove { }
        }

        public HtmlPage CreateSettingsPage(Action close) => new SettingsPage(ColorScheme, new BrowserSettings(), _ => null, close);

        public void OpenWindow(string? address, BrowserEngine? opener)
        {
        }

        public void OpenSettings()
        {
        }

        public void OpenAbout(BrowserEngine owner)
        {
        }

        public void OpenSiteInfo(BrowserEngine owner)
        {
        }
    }
}
