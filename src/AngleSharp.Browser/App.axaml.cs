namespace AngleSharp.Browser;

using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AngleSharp.Browser.Integration;
using AngleSharp.Browser.ViewModels;
using AngleSharp.Browser.Views;

public partial class App : Application
{
    private const string DefaultStartAddress = "https://me.florian-rappl.de";

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var arguments = CommandLine.Parse(desktop.Args ?? []);
            var window = new MainWindow();
            var screen = window.Screens.Primary?.Bounds;
            var options = new BrowserOptions
            {
                ProfileDirectory = arguments.IsInMemory ? null : arguments.ProfileDirectory ?? GetDefaultProfileDirectory(),
                ClipboardPlatform = window.Clipboard is { } clipboard ? new ClipboardPlatform(clipboard) : null,
                ScreenWidth = screen?.Width ?? 1920,
                ScreenHeight = screen?.Height ?? 1080,
            };
            desktop.MainWindow = window;
            // the lifetime shows the main window
            AttachWindow(window, new BrowserEngine(options), arguments.Address ?? DefaultStartAddress);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Sets up a browser window with its own engine, which goes away with the window. Windows
    /// the page asks for get engines sharing the profile of the one they were opened from.
    /// </summary>
    private static void AttachWindow(MainWindow window, BrowserEngine engine, string address)
    {
        var viewModel = new MainViewModel(engine, address);

        engine.WindowRequested += (_, request) =>
        {
            // raised on the engine's thread, while the request has to be answered right away
            request.Handled = true;
            Dispatcher.UIThread.Post(() => OpenWindow(engine, request.Url));
        };

        window.DataContext = viewModel;
        window.Opened += (_, _) => viewModel.NavigateCommand.Execute(null);
        window.Closed += (_, _) => engine.Dispose();
    }

    private static void OpenWindow(BrowserEngine opener, string address)
    {
        BrowserEngine engine;

        try
        {
            engine = opener.CreateWindow();
        }
        catch (ObjectDisposedException)
        {
            // the opening window was closed in the meantime
            return;
        }
        catch (Exception ex)
        {
            // a window that cannot be created must not take the others down with it
            Trace.WriteLine($"Opening a window failed: {ex}");
            return;
        }

        var window = new MainWindow();
        AttachWindow(window, engine, address);
        window.Show();
    }

    private static string GetDefaultProfileDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AngleSharp.Browser",
        "Default");

    /// <summary>
    /// Usage: AngleSharp.Browser [address] [--profile &lt;directory&gt;] [--in-memory]
    /// </summary>
    private sealed record CommandLine(string? Address, string? ProfileDirectory, bool IsInMemory)
    {
        public static CommandLine Parse(string[] args)
        {
            var result = new CommandLine(null, null, false);

            for (var i = 0; i < args.Length; i++)
            {
                result = args[i] switch
                {
                    "--in-memory" => result with { IsInMemory = true },
                    "--profile" when i + 1 < args.Length => result with { ProfileDirectory = args[++i] },
                    var address => result with { Address = address },
                };
            }

            return result;
        }
    }
}
