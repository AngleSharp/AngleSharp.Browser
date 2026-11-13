using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using NUnit.Framework;

namespace AngleSharp.Browser.Avalonia.Tests;

[TestFixture]
public class BrowserViewTests
{
    private static readonly string RedPage = CreatePage("Red", "rgb(255,0,0)");
    private static readonly string BluePage = CreatePage("Blue", "rgb(0,0,255)");

    [AvaloniaTest]
    public async Task SettingTheSourceCreatesAnEngineAndNavigates()
    {
        var view = new BrowserView { Source = RedPage };
        var window = Show(view);

        await WaitUntil(() => view.Engine?.Title == "Red");

        Assert.That(view.Engine!.Url, Is.EqualTo(RedPage));
        window.Close();
    }

    [AvaloniaTest]
    public async Task PressingAModifierOnItsOwnSendsItToThePage()
    {
        var page = "data:text/html," + Uri.EscapeDataString("<!doctype html><title>Keys</title><script>window.seen = []; document.addEventListener('keydown', function (e) { window.seen.push(e.key + ':' + e.keyCode); });</script>");
        var view = new BrowserView { Source = page };
        var window = Show(view);
        await WaitUntil(() => view.Engine?.Title == "Keys");
        view.Focus();

        // a game firing on Control (keyCode 17), like the Spaceshoot sample of Piral
        window.KeyPress(Key.LeftCtrl, RawInputModifiers.Control, PhysicalKey.ControlLeft, null);
        window.KeyPress(Key.RightShift, RawInputModifiers.Shift, PhysicalKey.ShiftRight, null);

        await WaitUntilAsync(async () => await view.Engine!.EvaluateScriptAsync("window.seen.join()") as string == "Control:17,Shift:16");
        window.Close();
    }

    [AvaloniaTest]
    public void WithoutASourceNoEngineIsCreated()
    {
        var view = new BrowserView();
        var window = Show(view);

        Assert.That(view.Engine, Is.Null);
        window.Close();
    }

    [AvaloniaTest]
    public async Task ChangingTheSourceNavigatesAgain()
    {
        var view = new BrowserView { Source = RedPage };
        var window = Show(view);
        await WaitUntil(() => view.Engine?.Title == "Red");
        var engine = view.Engine;

        view.Source = BluePage;
        await WaitUntil(() => view.Engine?.Title == "Blue");

        Assert.That(view.Engine, Is.SameAs(engine));
        Assert.That(engine!.CanGoBack, Is.True);
        window.Close();
    }

    [AvaloniaTest]
    public async Task TheSourceFollowsNavigationsOfTheEngine()
    {
        var view = new BrowserView { Source = RedPage };
        var window = Show(view);
        await WaitUntil(() => view.Engine?.Title == "Red");

        await view.Engine!.NavigateAsync(BluePage);
        await WaitUntil(() => view.Source == BluePage);

        Assert.That(view.Engine.Title, Is.EqualTo("Blue"));
        window.Close();
    }

    [AvaloniaTest]
    public async Task AnEngineCreatedByTheViewGoesAwayWhenDetachedAndComesBackWhenAttached()
    {
        var view = new BrowserView { Source = RedPage };
        var window = Show(view);
        await WaitUntil(() => view.Engine?.Title == "Red");
        var engine = view.Engine!;

        window.Content = null;

        Assert.That(view.Engine, Is.Null);
        Assert.That(await IsDisposedAsync(engine), Is.True);

        window.Content = view;
        await WaitUntil(() => view.Engine?.Title == "Red");

        Assert.That(view.Engine, Is.Not.SameAs(engine));
        window.Close();
    }

    [AvaloniaTest]
    public async Task AnEngineOfTheHostIsKeptWhenDetached()
    {
        using var engine = new BrowserEngine();
        var view = new BrowserView { Engine = engine };
        var window = Show(view);

        window.Content = null;
        await engine.LoadHtmlAsync("<title>Alive</title>");

        Assert.That(view.Engine, Is.SameAs(engine));
        Assert.That(engine.Title, Is.EqualTo("Alive"));
        window.Close();
    }

    [AvaloniaTest]
    public async Task AnEngineShowingAPageKeepsItAndUpdatesTheSource()
    {
        using var engine = new BrowserEngine();
        var view = new BrowserView { Source = RedPage };
        var window = Show(view);
        await WaitUntil(() => view.Engine?.Title == "Red");
        var created = view.Engine!;

        await engine.NavigateAsync(BluePage);
        view.Engine = engine;

        Assert.That(view.Source, Is.EqualTo(BluePage));
        Assert.That(engine.Title, Is.EqualTo("Blue"));
        Assert.That(await IsDisposedAsync(created), Is.True);
        window.Close();
    }

    [AvaloniaTest]
    public async Task AFreshEngineOpensTheSource()
    {
        using var engine = new BrowserEngine();
        var view = new BrowserView { Engine = engine, Source = RedPage };
        var window = Show(view);

        await WaitUntil(() => engine.Title == "Red");

        window.Close();
    }

    [AvaloniaTest]
    public async Task TheOptionsAreUsedForTheCreatedEngine()
    {
        var view = new BrowserView
        {
            Source = RedPage,
            Options = new BrowserOptions { ColorScheme = "dark", IsScriptingEnabled = false },
        };
        var window = Show(view);
        window.RequestedThemeVariant = ThemeVariant.Light;
        await WaitUntil(() => view.Engine?.Title == "Red");

        Assert.That(view.Engine!.ColorScheme, Is.EqualTo("dark"));
        window.Close();
    }

    [AvaloniaTest]
    public async Task TheColorSchemeOfACreatedEngineFollowsTheTheme()
    {
        var view = new BrowserView { Source = RedPage };
        var window = Show(view);
        window.RequestedThemeVariant = ThemeVariant.Light;
        await WaitUntil(() => view.Engine?.Title == "Red");
        Assert.That(view.Engine!.ColorScheme, Is.EqualTo("light"));

        window.RequestedThemeVariant = ThemeVariant.Dark;

        Assert.That(view.Engine.ColorScheme, Is.EqualTo("dark"));
        window.Close();
    }

    [AvaloniaTest]
    public async Task ThePageIsDisplayed()
    {
        var view = new BrowserView { Source = RedPage };
        var window = Show(view);

        await WaitUntil(() =>
        {
            using var frame = window.CaptureRenderedFrame();
            return frame is not null && IsRed(GetCenterPixel(frame));
        });

        window.Close();
    }

    [AvaloniaTest]
    public async Task ClickingADropdownShowsItsListAndChoosesARow()
    {
        var view = new BrowserView();
        var window = Show(view);
        var engine = new BrowserEngine(new BrowserOptions { IsScriptingEnabled = false });
        view.Engine = engine;
        await engine.LoadHtmlAsync("<style>html, body { margin: 0; height: 100%; background: rgb(255,0,0) } select { display: block; width: 300px; height: 20px }</style><select><option>a</option><option>b</option><option>c</option></select>");
        await WaitUntil(() => engine.HasPaintedDocument);

        // on the page, where the list goes
        const int X = 280;
        const int Y = 30;
        await WaitUntilAsync(async () => IsRed(await GetPixelAsync(window, X, Y)));

        window.MouseDown(new global::Avalonia.Point(10, 10), MouseButton.Left);
        window.MouseUp(new global::Avalonia.Point(10, 10), MouseButton.Left);
        await WaitUntil(() => engine.IsSelectPopupOpen);
        await WaitUntilAsync(async () => !IsRed(await GetPixelAsync(window, X, Y)));

        // the second row
        window.MouseDown(new global::Avalonia.Point(10, 20 + 1 + (1.5 * 19)), MouseButton.Left);
        window.MouseUp(new global::Avalonia.Point(10, 20 + 1 + (1.5 * 19)), MouseButton.Left);
        await WaitUntil(() => !engine.IsSelectPopupOpen);
        await WaitUntilAsync(async () => IsRed(await GetPixelAsync(window, X, Y)));

        var value = await engine.InvokeAsync(document => ((AngleSharp.Html.Dom.IHtmlSelectElement)document!.QuerySelector("select")!).Value);
        Assert.That(value, Is.EqualTo("b"));
        window.Close();
        engine.Dispose();
    }

    private static async Task<byte[]> GetPixelAsync(Window window, int x, int y)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        using var frame = window.CaptureRenderedFrame();
        return frame is null ? new byte[4] : GetPixel(frame, x, y);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        while (!await condition())
        {
            Assert.That(watch.ElapsedMilliseconds, Is.LessThan(10000), "Timed out.");
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static unsafe byte[] GetPixel(WriteableBitmap frame, int x, int y)
    {
        using var buffer = frame.Lock();
        var pixel = (byte*)buffer.Address + (y * buffer.RowBytes) + (x * 4);
        return buffer.Format == PixelFormat.Bgra8888 ? [pixel[0], pixel[1], pixel[2], pixel[3]] : [pixel[2], pixel[1], pixel[0], pixel[3]];
    }

    private static string CreatePage(string title, string background) =>
        "data:text/html," + Uri.EscapeDataString($"<!doctype html><html style='background:{background}'><title>{title}</title></html>");

    private static Window Show(BrowserView view)
    {
        var window = new Window { Width = 400, Height = 300, Content = view };
        window.Show();
        return window;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(30);

        while (!condition())
        {
            if (DateTime.UtcNow > timeout)
            {
                Assert.Fail("The condition was not met in time.");
            }

            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    // an awaited try/catch, as the asserts for async code block the dispatcher running the test
    private static async Task<bool> IsDisposedAsync(BrowserEngine engine)
    {
        try
        {
            await engine.PrepareFrameAsync();
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static bool IsRed(byte[] bgra) => bgra[2] > 200 && bgra[1] < 50 && bgra[0] < 50;

    private static unsafe byte[] GetCenterPixel(WriteableBitmap frame)
    {
        using var buffer = frame.Lock();
        var row = (byte*)buffer.Address + buffer.RowBytes * (buffer.Size.Height / 2);
        var pixel = row + 4 * (buffer.Size.Width / 2);
        // the channel order depends on the platform; red and blue are what the test tells apart
        var isBgra = buffer.Format == PixelFormat.Bgra8888;
        return isBgra ? [pixel[0], pixel[1], pixel[2], pixel[3]] : [pixel[2], pixel[1], pixel[0], pixel[3]];
    }
}
