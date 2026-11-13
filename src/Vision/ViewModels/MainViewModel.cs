namespace Vision.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using AngleSharp.Browser;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.IO;
using Vision.Models;
using Vision.Pages;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private const string ApplicationName = "Vision";
    private const int SuggestionCount = 8;

    private readonly IBrowserHost _host;
    private readonly UrlHistory _history;
    private string? _recordedUrl;
    private bool _isShowingAddress;
    private HtmlPage? _panel;
    private string? _lastUrl;
    private int _isErrorCheckPending;
    private string? _iconUrl;
    private CancellationTokenSource? _iconLoading;

    public MainViewModel(BrowserEngine engine, string startAddress, IBrowserHost host, UrlHistory? history = null)
    {
        Engine = engine;
        _history = history ?? new UrlHistory();
        ShowAddress(startAddress);
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
    /// Gets the icon of the page, once it has been loaded.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPageIcon))]
    public partial Bitmap? PageIcon { get; set; }

    public bool HasPageIcon => PageIcon is not null;

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

    /// <summary>
    /// Gets the addresses visited before, which match what is typed into the address bar.
    /// </summary>
    public ObservableCollection<UrlSuggestion> Suggestions { get; } = [];

    /// <summary>
    /// Gets if the address bar shows its suggestions.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSuggesting { get; set; }

    /// <summary>
    /// Gets the index of the suggestion chosen with the arrow keys, or -1 for what is typed.
    /// </summary>
    [ObservableProperty]
    public partial int SelectedSuggestionIndex { get; set; } = -1;

    public bool HasStatus => Status.Length > 0;

    /// <summary>
    /// Gets if there are errors the diagnostics would show, but are not showing right now.
    /// </summary>
    public bool NeedsAttention => HasErrors && !IsDiagnosticsOpen;

    public void Dispose()
    {
        Engine.Diagnostics.Changed -= OnDiagnosticsChanged;
        _host.ColorSchemeChanged -= OnColorSchemeChanged;
        _iconLoading?.Cancel();
        ClosePanel();
    }

    /// <summary>
    /// Moves the selection through the suggestions (from what is typed to the first one, and
    /// back), if they are shown.
    /// </summary>
    /// <returns>If the suggestions handled the move.</returns>
    public bool MoveSuggestion(int delta)
    {
        if (!IsSuggesting || Suggestions.Count == 0)
        {
            return false;
        }

        // -1 is what has been typed, which sits above the first suggestion
        var count = Suggestions.Count + 1;
        SelectedSuggestionIndex = ((SelectedSuggestionIndex + 1 + delta) % count + count) % count - 1;
        return true;
    }

    /// <summary>
    /// Hides the suggestions.
    /// </summary>
    /// <returns>If they were shown.</returns>
    public bool CloseSuggestions()
    {
        var wasSuggesting = IsSuggesting;
        IsSuggesting = false;
        SelectedSuggestionIndex = -1;
        Suggestions.Clear();
        return wasSuggesting;
    }

    /// <summary>
    /// Navigates to the given suggestion, e.g., when it was clicked.
    /// </summary>
    public Task NavigateToAsync(UrlSuggestion suggestion)
    {
        CloseSuggestions();
        ShowAddress(suggestion.Url);
        return Engine.NavigateAsync(suggestion.Url);
    }

    /// <summary>
    /// Navigates to the chosen suggestion, else to what has been typed.
    /// </summary>
    [RelayCommand]
    private Task NavigateAsync()
    {
        if (IsSuggesting && SelectedSuggestionIndex >= 0 && SelectedSuggestionIndex < Suggestions.Count)
        {
            return NavigateToAsync(Suggestions[SelectedSuggestionIndex]);
        }

        CloseSuggestions();
        ShowAddress(BrowserEngine.NormalizeAddress(Address, Engine.Schemes));
        return Engine.NavigateAsync(Address);
    }

    partial void OnAddressChanged(string value)
    {
        // only what the user types is looked up
        if (!_isShowingAddress)
        {
            UpdateSuggestions(value);
        }
    }

    private void UpdateSuggestions(string text)
    {
        var suggestions = _history.Suggest(text, SuggestionCount);
        SelectedSuggestionIndex = -1;
        Suggestions.Clear();

        foreach (var suggestion in suggestions)
        {
            Suggestions.Add(suggestion);
        }

        IsSuggesting = Suggestions.Count > 0;
    }

    /// <summary>
    /// Shows an address in the address bar, which is not looked up like a typed one.
    /// </summary>
    private void ShowAddress(string address)
    {
        _isShowingAddress = true;

        try
        {
            Address = address;
        }
        finally
        {
            _isShowingAddress = false;
        }
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
    private void OpenSiteInfo() => _host.OpenSiteInfo(Engine);

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
            CloseSuggestions();
            ShowAddress(url);
        }

        RecordVisit();

        var isSecure = Engine.Url?.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ?? false;
        IsSecure = isSecure;
        SiteInfo = isSecure ? "Connection is secure"
            : Engine.Url?.StartsWith($"{VisionPages.Scheme}:", StringComparison.OrdinalIgnoreCase) ?? false ? "Vision's own page"
            : "Connection is not secure";
        WindowTitle = string.IsNullOrWhiteSpace(Engine.Title) ? ApplicationName : $"{Engine.Title} - {ApplicationName}";

        if (Engine.IconUrl != _iconUrl)
        {
            _iconUrl = Engine.IconUrl;
            _ = LoadIconAsync();
        }

        // a page has loaded once it is visible, which needs its first frame
        IsLoading = Engine.IsLoading || !Engine.HasPaintedDocument;
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
        UpdateStatus();
    }

    /// <summary>
    /// Counts the visit of a page once it has loaded (also of an address a script pushed), and
    /// keeps its title, which the suggestions show.
    /// </summary>
    private void RecordVisit()
    {
        if (Engine.IsLoading || Engine.Url is not string url)
        {
            return;
        }

        if (url != _recordedUrl)
        {
            _recordedUrl = url;
            _history.Record(url, Engine.Title);
        }
        else
        {
            _history.SetTitle(url, Engine.Title);
        }
    }

    /// <summary>
    /// Loads the icon of the current page, which replaces the one of the previous page.
    /// </summary>
    private async Task LoadIconAsync()
    {
        _iconLoading?.Cancel();
        var loading = _iconLoading = new CancellationTokenSource();
        Bitmap? icon = null;

        try
        {
            if (await Engine.LoadIconAsync(loading.Token) is { } content)
            {
                // bitmaps of any kind (PNG, ICO, ...) but SVG, sized for the toolbar and the title bar
                using var stream = new MemoryStream(content);
                icon = Bitmap.DecodeToWidth(stream, 64, BitmapInterpolationMode.HighQuality);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.WriteLine($"Loading the page icon failed: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!loading.IsCancellationRequested)
        {
            PageIcon = icon;
        }
    }

    private void UpdateStatus()
    {
        Status = Engine.HoveredLink ?? (IsLoading ? $"Loading {Address}" : string.Empty);
    }
}
