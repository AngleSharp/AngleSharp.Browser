using System;
using System.ComponentModel;
using Vision.ViewModels;
using Avalonia.Controls;

namespace Vision.Views;

public partial class MainWindow : Window
{
    private const double DefaultPanelWidth = 200;
    private const double MinPanelWidth = 160;
    private const double MaxPanelShare = 0.8;

    private MainViewModel? _viewModel;
    // what the user made of the panel's width, which is kept for the next time
    private double? _panelWidth;
    private double _openedWidth;

    public MainWindow()
    {
        InitializeComponent();
        Icon = AppIcon.Default;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as MainViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
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
