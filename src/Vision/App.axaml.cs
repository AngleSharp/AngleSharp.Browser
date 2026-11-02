namespace Vision;

using System;
using AngleSharp.Browser;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using AngleSharp.Browser.Avalonia;
using Vision.Models;
using Vision.Pages;
using Vision.ViewModels;
using Vision.Views;

public partial class App : Application, IBrowserHost
{
    private readonly List<BrowserWindow> _windows = [];
    private BrowserSettings _settings = new();
    private CommandLine _arguments = new(null, null, false);
    private PageWindow? _settingsWindow;

    public event EventHandler? ColorSchemeChanged;

    public string ColorScheme => ActualThemeVariant == ThemeVariant.Dark ? "dark" : "light";

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _arguments = CommandLine.Parse(desktop.Args ?? []);
            _settings = BrowserSettings.Load();
            ApplyTheme();
            ActualThemeVariantChanged += (_, _) => ColorSchemeChanged?.Invoke(this, EventArgs.Empty);
            // the lifetime shows the main window
            desktop.MainWindow = CreateWindow(_arguments.Address, null);
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void OpenWindow(string? address, BrowserEngine? opener)
    {
        var window = CreateWindow(address, opener);
        window?.Show();
    }

    public void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var page = new SettingsPage(ColorScheme, _settings, ApplySettings, () => _settingsWindow?.Close());
        _settingsWindow = new PageWindow(page, this)
        {
            Title = "Settings - Vision",
            Width = 720,
            Height = 760,
            MinWidth = 420,
            MinHeight = 320,
        };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;

        if (GetActiveWindow() is { } owner)
        {
            _settingsWindow.Show(owner);
        }
        else
        {
            _settingsWindow.Show();
        }
    }

    public void OpenAbout(BrowserEngine owner)
    {
        PageWindow? window = null;
        var page = new AboutPage(ColorScheme, () => window?.Close());
        // the links of the page open in browser windows
        page.Engine.WindowRequested += (_, request) =>
        {
            request.Handled = true;
            Dispatcher.UIThread.Post(() => OpenWindow(request.Url, null));
        };
        window = new PageWindow(page, this)
        {
            Title = "About Vision",
            Width = 560,
            Height = 720,
            CanResize = false,
        };

        if (_windows.FirstOrDefault(m => m.Engine == owner)?.Window is { } ownerWindow)
        {
            window.ShowDialog(ownerWindow);
        }
        else
        {
            window.Show();
        }
    }

    /// <summary>
    /// Saves the settings, which every window opened from now on uses.
    /// </summary>
    private string? ApplySettings(BrowserSettings settings)
    {
        try
        {
            settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The settings could not be saved: {ex.Message}";
        }

        _settings = settings;
        ApplyTheme();
        return null;
    }

    private void ApplyTheme()
    {
        RequestedThemeVariant = _settings.Theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    /// <summary>
    /// Sets up a browser window with its own engine, which goes away with the window. Windows
    /// share the profile data of another window using the same profile, ideally the opener.
    /// </summary>
    private MainWindow? CreateWindow(string? address, BrowserEngine? opener)
    {
        var window = new MainWindow();
        var options = _settings.CreateOptions();
        var screen = window.Screens.Primary?.Bounds;

        if (_arguments.IsInMemory)
        {
            options.ProfileDirectory = null;
        }
        else if (_arguments.ProfileDirectory is not null)
        {
            options.ProfileDirectory = _arguments.ProfileDirectory;
        }

        options.ClipboardPlatform = window.Clipboard is { } clipboard ? new AvaloniaClipboardPlatform(clipboard) : null;
        options.ScreenWidth = screen?.Width ?? 1920;
        options.ScreenHeight = screen?.Height ?? 1080;
        options.ColorScheme = ColorScheme;

        // in-memory profiles are only shared with the opener, a directory with any window using it
        var profileOwner = _windows.FirstOrDefault(m => m.Engine == opener && m.ProfileDirectory == options.ProfileDirectory)
            ?? (options.ProfileDirectory is null ? null : _windows.FirstOrDefault(m => m.ProfileDirectory == options.ProfileDirectory));
        BrowserEngine engine;

        try
        {
            engine = profileOwner?.Engine.CreateWindow(options) ?? new BrowserEngine(options);
        }
        catch (ObjectDisposedException)
        {
            // the window sharing the profile was closed in the meantime
            engine = new BrowserEngine(options);
        }
        catch (Exception ex)
        {
            // a window that cannot be created must not take the others down with it
            Trace.WriteLine($"Opening a window failed: {ex}");
            return null;
        }

        var entry = new BrowserWindow(window, engine, options.ProfileDirectory);
        var viewModel = new MainViewModel(engine, address ?? _settings.StartPage, this);

        engine.WindowRequested += (_, request) =>
        {
            // raised on the engine's thread, while the request has to be answered right away
            request.Handled = true;
            Dispatcher.UIThread.Post(() => OpenWindow(request.Url, engine));
        };

        _windows.Add(entry);
        window.DataContext = viewModel;
        window.Opened += (_, _) => viewModel.NavigateCommand.Execute(null);
        window.Closed += (_, _) =>
        {
            _windows.Remove(entry);
            viewModel.Dispose();
            engine.Dispose();
        };
        return window;
    }

    private MainWindow? GetActiveWindow() => _windows.FirstOrDefault(m => m.Window.IsActive)?.Window ?? _windows.FirstOrDefault()?.Window;

    private sealed record BrowserWindow(MainWindow Window, BrowserEngine Engine, string? ProfileDirectory);

    /// <summary>
    /// Usage: Vision [address] [--profile &lt;directory&gt;] [--in-memory]
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
