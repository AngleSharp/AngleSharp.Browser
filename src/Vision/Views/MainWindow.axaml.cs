using System;
using System.ComponentModel;
using Vision.Models;
using Vision.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Vision.Views;

public partial class MainWindow : Window
{
    private const double DefaultPanelWidth = 200;
    private const double MinPanelWidth = 160;
    private const double MaxPanelShare = 0.8;
    private static readonly TimeSpan HoldDuration = TimeSpan.FromMilliseconds(450);

    private MainViewModel? _viewModel;
    // what the user made of the panel's width, which is kept for the next time
    private double? _panelWidth;
    private double _openedWidth;
    // holding the back or forward button down opens its history instead of going there
    private readonly DispatcherTimer _holdTimer;
    private Button? _heldButton;
    private bool _isHoldHandled;
    // the state of the window before the page showed something fullscreen
    private WindowState? _stateBeforeFullscreen;

    public MainWindow()
    {
        InitializeComponent();
        Icon = AppIcon.Default;
        // before the text box uses the keys to move the caret
        AddressBox.AddHandler(KeyDownEvent, OnAddressKeyDown, RoutingStrategies.Tunnel);
        AddressBox.LostFocus += OnAddressLostFocus;
        SuggestionList.AddHandler(PointerPressedEvent, OnSuggestionPressed, RoutingStrategies.Tunnel);
        _holdTimer = new DispatcherTimer { Interval = HoldDuration };
        _holdTimer.Tick += OnHoldElapsed;

        foreach (var button in (Button[])[BackButton, ForwardButton])
        {
            // before the button takes the press, and also once it has
            button.AddHandler(PointerPressedEvent, OnHistoryButtonPressed, RoutingStrategies.Tunnel);
            button.AddHandler(PointerReleasedEvent, OnHistoryButtonReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            button.AddHandler(PointerCaptureLostEvent, OnHistoryButtonReleased, RoutingStrategies.Direct | RoutingStrategies.Bubble, handledEventsToo: true);
            button.Click += OnHistoryButtonClick;
        }
    }

    private void OnHistoryButtonPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        var properties = e.GetCurrentPoint(button).Properties;
        _isHoldHandled = false;

        if (properties.IsRightButtonPressed)
        {
            // like the context menu of the button
            e.Handled = true;
            ShowHistoryMenu(button);
        }
        else if (properties.IsLeftButtonPressed)
        {
            _heldButton = button;
            _holdTimer.Stop();
            _holdTimer.Start();
        }
    }

    private void OnHistoryButtonReleased(object? sender, RoutedEventArgs e)
    {
        _holdTimer.Stop();
        _heldButton = null;
    }

    private void OnHoldElapsed(object? sender, EventArgs e)
    {
        _holdTimer.Stop();

        if (_heldButton is { } button)
        {
            _heldButton = null;
            // the release is no click anymore
            _isHoldHandled = true;
            ShowHistoryMenu(button);
        }
    }

    private void OnHistoryButtonClick(object? sender, RoutedEventArgs e)
    {
        if (_isHoldHandled)
        {
            // the button runs its command unless the click was handled
            _isHoldHandled = false;
            e.Handled = true;
        }
    }

    /// <summary>
    /// Shows the entries of the history the button jumps to, the closest first.
    /// </summary>
    private void ShowHistoryMenu(Button button)
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        var jumps = viewModel.GetHistoryJumps(button == BackButton);

        if (jumps.Count == 0)
        {
            return;
        }

        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };

        foreach (var jump in jumps)
        {
            var item = new MenuItem { Header = jump.Heading, MaxWidth = 420 };
            ToolTip.SetTip(item, jump.Url);
            item.Click += (_, _) => _ = viewModel.JumpAsync(jump);
            menu.Items.Add(item);
        }

        menu.ShowAt(button);
    }

    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is { } viewModel && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = e.Key switch
            {
                Key.Down => viewModel.MoveSuggestion(1),
                Key.Up => viewModel.MoveSuggestion(-1),
                Key.Escape => viewModel.CloseSuggestions(),
                _ => false,
            };
        }
    }

    private void OnAddressLostFocus(object? sender, RoutedEventArgs e)
    {
        // a click on a suggestion comes first
        Dispatcher.UIThread.Post(() =>
        {
            if (!AddressBox.IsFocused)
            {
                _viewModel?.CloseSuggestions();
            }
        }, DispatcherPriority.Background);
    }

    private void OnSuggestionPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is { } viewModel && e.GetCurrentPoint(SuggestionList).Properties.IsLeftButtonPressed &&
            (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is UrlSuggestion suggestion)
        {
            e.Handled = true;
            _ = viewModel.NavigateToAsync(suggestion);
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Engine.FullscreenChanged -= OnFullscreenChanged;
        }

        _viewModel = DataContext as MainViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.Engine.FullscreenChanged += OnFullscreenChanged;
        }
    }

    // raised on the engine's thread
    private void OnFullscreenChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => ShowFullscreen(_viewModel?.Engine.IsFullscreen == true));

    /// <summary>
    /// Shows the window fullscreen with just the page while the page shows something
    /// fullscreen, and as before afterwards.
    /// </summary>
    private void ShowFullscreen(bool isFullscreen)
    {
        if (isFullscreen && _stateBeforeFullscreen is null)
        {
            _stateBeforeFullscreen = WindowState;
            Chrome.IsVisible = false;
            ContentGrid.ColumnDefinitions[2].Width = new GridLength(0);
            WindowState = WindowState.FullScreen;
        }
        else if (!isFullscreen && _stateBeforeFullscreen is { } state)
        {
            _stateBeforeFullscreen = null;
            Chrome.IsVisible = true;
            WindowState = state;

            if (_viewModel?.IsPanelOpen == true)
            {
                ContentGrid.ColumnDefinitions[2].Width = new GridLength(_openedWidth);
            }
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // the user leaving the fullscreen window (e.g. with a key of the platform) ends it for the page as well
        if (change.Property == WindowStateProperty && _stateBeforeFullscreen is not null && WindowState != WindowState.FullScreen)
        {
            _stateBeforeFullscreen = null;
            Chrome.IsVisible = true;
            _ = _viewModel?.Engine.ExitFullscreenAsync();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.PageIcon) && AppIcon.CanShowPageIcon)
        {
            // the title bar shows the page's icon, like the tab of other browsers
            Icon = _viewModel!.PageIcon is { } icon ? new WindowIcon(icon) : AppIcon.Default;
        }

        if (e.PropertyName == nameof(MainViewModel.IsPanelOpen))
        {
            var column = ContentGrid.ColumnDefinitions[2];

            if (_viewModel!.IsPanelOpen)
            {
                // the splitter resizes the column
                var available = ContentGrid.Bounds.Width;
                var width = _panelWidth ?? Math.Max(DefaultPanelWidth, available / 3);
                _openedWidth = available > 0 ? Math.Min(width, Math.Max(MinPanelWidth, available * MaxPanelShare)) : width;
                column.MinWidth = MinPanelWidth;
                column.Width = new GridLength(_openedWidth);
            }
            else
            {
                if (column.ActualWidth > 0 && Math.Abs(column.ActualWidth - _openedWidth) > 0.5)
                {
                    _panelWidth = column.ActualWidth;
                }

                column.MinWidth = 0;
                column.Width = new GridLength(0);
            }
        }
    }
}
