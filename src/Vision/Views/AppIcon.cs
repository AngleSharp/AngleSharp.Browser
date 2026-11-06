namespace Vision.Views;

using System;
using Avalonia.Controls;
using Avalonia.Platform;

/// <summary>
/// The icon of Vision's windows. On macOS a window's icon becomes the icon in the Dock, which
/// is drawn with the margin and shadow of other macOS icons.
/// </summary>
public static class AppIcon
{
    private static readonly Lazy<WindowIcon> Icon = new(() => new WindowIcon(AssetLoader.Open(
        new Uri(OperatingSystem.IsMacOS() ? "avares://Vision/Assets/vision.png" : "avares://Vision/Assets/vision.ico"))));

    public static WindowIcon Default => Icon.Value;

    /// <summary>
    /// Gets if a window may show the icon of its page, which macOS would show in the Dock.
    /// </summary>
    public static bool CanShowPageIcon => !OperatingSystem.IsMacOS();
}
