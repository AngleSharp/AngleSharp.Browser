namespace Vision.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using AngleSharp.Browser;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Vision.Pages;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private const string ApplicationName = "Vision";

    private readonly IBrowserHost _host;
    private HtmlPage? _panel;
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
    [NotifyPropertyChangedFor(nameof(NeedsAttention))]
    public partial bool HasErrors { get; set; }

    /// <summary>
    /// Gets if the side panel shows the diagnostics.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsAttention))]
    public partial bool IsDiagnosticsOpen { get; set; }

    /// <summary>
    /// Gets if the side panel shows the inspector.
    /// </summary>
    [ObservableProperty]
    public partial bool IsInspectorOpen { get; set; }

    /// <summary>
    /// Gets if the side panel is open.
    /// </summary>
    [ObservableProperty]
    public partial bool IsPanelOpen { get; set; }

    /// <summary>
    /// Gets the engine showing the side panel, while it is open.
    /// </summary>
    [ObservableProperty]
    public partial BrowserEngine? PanelEngine { get; set; }

    public bool HasStatus => Status.Length > 0;

    /// <summary>
    /// Gets if there are errors the diagnostics would show, but are not showing right now.
    /// </summary>
    public bool NeedsAttention => HasErrors && !IsDiagnosticsOpen;

    public void Dispose()
    {
        Engine.Diagnostics.Changed -= OnDiagnosticsChanged;
        _host.ColorSchemeChanged -= OnColorSchemeChanged;
        ClosePanel();
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
    private Task ToggleDiagnosticsAsync() => TogglePanelAsync<DiagnosticsPage>(() => new DiagnosticsPage(_host.ColorScheme, Engine));

    [RelayCommand]
    private Task ToggleInspectorAsync() => TogglePanelAsync<InspectorPage>(() => new InspectorPage(_host.ColorScheme, Engine));

    /// <summary>
    /// The side panel shows one page: the given one is closed if it is shown, else it replaces
    /// whatever else is shown.
    /// </summary>
    private async Task TogglePanelAsync<T>(Func<T> create)
        where T : HtmlPage
    {
        var wasOpen = _panel is T;
        ClosePanel();

        if (!wasOpen)
        {
            var page = create();
            _panel = page;
            PanelEngine = page.Engine;
            IsDiagnosticsOpen = page is DiagnosticsPage;
            IsInspectorOpen = page is InspectorPage;
            IsPanelOpen = true;
            await page.LoadAsync();
        }
    }

    private void ClosePanel()
    {
        if (_panel is { } page)
        {
            _panel = null;
            IsPanelOpen = false;
            IsDiagnosticsOpen = false;
            IsInspectorOpen = false;
            PanelEngine = null;
            page.Dispose();
        }
    }

    private bool CanGoBack() => Engine.CanGoBack;

    private bool CanGoForward() => Engine.CanGoForward;

    private void OnColorSchemeChanged(object? sender, EventArgs e)
    {
        Engine.ColorScheme = _host.ColorScheme;

        if (PanelEngine is { } panel)
        {
            panel.ColorScheme = _host.ColorScheme;
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
