namespace AngleSharp.Browser.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using AngleSharp.Browser.Pages;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private const string ApplicationName = "AngleSharp.Browser";

    private readonly IBrowserHost _host;
    private DiagnosticsPage? _diagnostics;
    private string? _lastUrl;
    private int _isErrorCheckPending;

    public MainViewModel(BrowserEngine engine, string startAddress, IBrowserHost host)
    {
        Engine = engine;
        Address = startAddress;
        _host = host;
        engine.ColorScheme = host.ColorScheme;
        engine.StateChanged += (_, _) => Dispatcher.UIThread.Post(UpdateState);
        engine.HoverChanged += (_, _) => Dispatcher.UIThread.Post(UpdateStatus);
        engine.Diagnostics.Changed += OnDiagnosticsChanged;
        host.ColorSchemeChanged += OnColorSchemeChanged;
    }

    public BrowserEngine Engine { get; }

    [ObservableProperty]
    public partial string Address { get; set; }

    [ObservableProperty]
    public partial string WindowTitle { get; set; } = ApplicationName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsSecure { get; set; }

    [ObservableProperty]
    public partial string SiteInfo { get; set; } = string.Empty;

    /// <summary>
    /// Gets if the page reported errors, which the diagnostics show.
    /// </summary>
    [ObservableProperty]
    public partial bool HasErrors { get; set; }

    [ObservableProperty]
    public partial bool IsDiagnosticsOpen { get; set; }

    /// <summary>
    /// Gets the engine showing the diagnostics of the page, while they are open.
    /// </summary>
    [ObservableProperty]
    public partial BrowserEngine? DiagnosticsEngine { get; set; }

    public bool HasStatus => Status.Length > 0;

    public void Dispose()
    {
        Engine.Diagnostics.Changed -= OnDiagnosticsChanged;
        _host.ColorSchemeChanged -= OnColorSchemeChanged;
        CloseDiagnostics();
    }

    [RelayCommand]
    private Task NavigateAsync()
    {
        Address = BrowserEngine.NormalizeAddress(Address);
        return Engine.NavigateAsync(Address);
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private Task GoBackAsync() => Engine.GoBackAsync();

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private Task GoForwardAsync() => Engine.GoForwardAsync();

    [RelayCommand]
    private Task ReloadAsync() => Engine.ReloadAsync();

    [RelayCommand]
    private void NewWindow() => _host.OpenWindow(null, Engine);

    [RelayCommand]
    private void OpenSettings() => _host.OpenSettings();

    [RelayCommand]
    private void OpenAbout() => _host.OpenAbout(Engine);

    [RelayCommand]
    private async Task ToggleDiagnosticsAsync()
    {
        if (_diagnostics is not null)
        {
            CloseDiagnostics();
            return;
        }

        var page = new DiagnosticsPage(_host.ColorScheme, Engine);
        _diagnostics = page;
        DiagnosticsEngine = page.Engine;
        IsDiagnosticsOpen = true;
        await page.LoadAsync();
    }

    private void CloseDiagnostics()
    {
        if (_diagnostics is { } page)
        {
            _diagnostics = null;
            IsDiagnosticsOpen = false;
            DiagnosticsEngine = null;
            page.Dispose();
        }
    }

    private bool CanGoBack() => Engine.CanGoBack;

    private bool CanGoForward() => Engine.CanGoForward;

    private void OnColorSchemeChanged(object? sender, EventArgs e)
    {
        Engine.ColorScheme = _host.ColorScheme;

        if (DiagnosticsEngine is { } diagnostics)
        {
            diagnostics.ColorScheme = _host.ColorScheme;
        }
    }

    private void OnDiagnosticsChanged(object? sender, EventArgs e)
    {
        // pages report a lot while loading; one check on the UI thread covers them all
        if (Interlocked.Exchange(ref _isErrorCheckPending, 1) == 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Volatile.Write(ref _isErrorCheckPending, 0);
                HasErrors = Engine.Diagnostics.Messages.Any(m => m.Kind == DiagnosticKind.Error);
            }, DispatcherPriority.Background);
        }
    }

    private void UpdateState()
    {
        // only a new address replaces what may be typed into the box
        if (Engine.Url is string url && url != "about:blank" && url != _lastUrl)
        {
            _lastUrl = url;
            Address = url;
        }

        var isSecure = Engine.Url?.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ?? false;
        IsSecure = isSecure;
        SiteInfo = isSecure ? "Connection is secure" : "Connection is not secure";
        WindowTitle = string.IsNullOrWhiteSpace(Engine.Title) ? ApplicationName : $"{Engine.Title} - {ApplicationName}";
        // a page has loaded once it is visible, which needs its first frame
        IsLoading = Engine.IsLoading || !Engine.HasPaintedDocument;
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        Status = Engine.HoveredLink ?? (IsLoading ? $"Loading {Address}" : string.Empty);
    }
}
