namespace Vision.Models;

using System;
using AngleSharp.Browser;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// The user's settings, which apply to every browsing context (window) created after they were
/// saved. Stored as JSON in the application's data directory.
/// </summary>
public sealed record BrowserSettings
{
    public const string DefaultStartPage = "https://me.florian-rappl.de";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Gets the page new windows start with.
    /// </summary>
    public string StartPage { get; init; } = DefaultStartPage;

    /// <summary>
    /// Gets if pages may run JavaScript.
    /// </summary>
    public bool IsScriptingEnabled { get; init; } = true;

    /// <summary>
    /// Gets if cookies, the web storage and the HTTP cache are kept on disk.
    /// </summary>
    public bool IsPersistent { get; init; } = true;

    /// <summary>
    /// Gets where cookies, the web storage and the HTTP cache are kept; null for the default.
    /// </summary>
    public string? ProfileDirectory { get; init; }

    /// <summary>
    /// Gets the user agent to report; null for the default of AngleSharp.
    /// </summary>
    public string? UserAgent { get; init; }

    /// <summary>
    /// Gets if requests ask sites not to track the user (DNT header).
    /// </summary>
    public bool IsDoNotTrackEnabled { get; init; }

    /// <summary>
    /// Gets the headers sent with every request.
    /// </summary>
    public IReadOnlyDictionary<string, string> DefaultHeaders { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Gets the default font size in CSS pixels.
    /// </summary>
    public double FontSize { get; init; } = 16;

    /// <summary>
    /// Gets the theme of the browser and the color scheme reported to pages.
    /// </summary>
    public AppTheme Theme { get; init; } = AppTheme.System;

    public static string DefaultProfileDirectory { get; } = Path.Combine(DataDirectory, "Default");

    private static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vision");

    private static string FilePath => Path.Combine(DataDirectory, "settings.json");

    public static BrowserSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<BrowserSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // broken settings must not keep the browser from starting
            Trace.WriteLine($"Loading the settings failed: {ex}");
        }

        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>
    /// Creates the options of a new browsing context from these settings.
    /// </summary>
    public BrowserOptions CreateOptions()
    {
        var options = new BrowserOptions
        {
            ProfileDirectory = IsPersistent ? ProfileDirectory ?? DefaultProfileDirectory : null,
            IsScriptingEnabled = IsScriptingEnabled,
            UserAgent = UserAgent,
            FontSize = FontSize,
        };

        foreach (var header in DefaultHeaders)
        {
            options.DefaultHeaders[header.Key] = header.Value;
        }

        if (IsDoNotTrackEnabled)
        {
            options.DefaultHeaders["DNT"] = "1";
        }

        return options;
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
public enum AppTheme
{
    System,
    Light,
    Dark,
}
