namespace AngleSharp.Browser.Pages;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AngleSharp.Browser.Models;
using AngleSharp.Dom;

/// <summary>
/// The settings window: an HTML form, which .NET fills from the settings and reads back when
/// the user applies it. Applying runs on the UI thread and answers a problem, if any.
/// </summary>
public sealed class SettingsPage(string? colorScheme, BrowserSettings settings, Func<BrowserSettings, string?> apply, Action close)
    : HtmlPage("settings", colorScheme)
{
    private static readonly Dictionary<string, string?> UserAgents = new()
    {
        ["ua-default"] = null,
        ["ua-chrome"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36",
        ["ua-firefox"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:143.0) Gecko/20100101 Firefox/143.0",
        ["ua-safari"] = "Mozilla/5.0 (Macintosh; Intel Mac OS X 15_6) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15",
    };

    private static readonly AppTheme[] Themes = Enum.GetValues<AppTheme>();

    protected override void Wire(IDocument document)
    {
        Fill(document, settings);

        foreach (var (id, userAgent) in UserAgents)
        {
            OnClick(document, id, () => SetValue(document, "user-agent", userAgent ?? string.Empty));
        }

        OnClick(document, "reset", () =>
        {
            Fill(document, new BrowserSettings());
            ShowStatus(document, "Defaults restored. Apply to keep them.", null);
        });
        OnClick(document, "close", () => OnUiThread(close));
        OnClick(document, "apply", () =>
        {
            if (TryRead(document, out var changed, out var problem))
            {
                OnUiThread(async () =>
                {
                    var failure = apply(changed);
                    await Engine.InvokeAsync(_ => ShowStatus(document, failure ?? "Saved. New windows use these settings.", failure is null ? "saved" : "invalid"));
                });
            }
            else
            {
                ShowStatus(document, problem, "invalid");
            }
        });
    }

    private static void Fill(IDocument document, BrowserSettings settings)
    {
        SetValue(document, "start-page", settings.StartPage);
        SetChecked(document, "scripting", settings.IsScriptingEnabled);
        SetValue(document, "font-size", settings.FontSize.ToString(CultureInfo.InvariantCulture));
        SetChecked(document, "persistent", settings.IsPersistent);
        SetValue(document, "profile-directory", settings.ProfileDirectory ?? string.Empty);
        SetChecked(document, "do-not-track", settings.IsDoNotTrackEnabled);
        SetValue(document, "user-agent", settings.UserAgent ?? string.Empty);
        SetValue(document, "headers", string.Join("\n", settings.DefaultHeaders.Select(m => $"{m.Key}: {m.Value}")));

        foreach (var theme in Themes)
        {
            SetChecked(document, GetThemeId(theme), theme == settings.Theme);
        }
    }

    private static bool TryRead(IDocument document, out BrowserSettings settings, out string problem)
    {
        settings = new BrowserSettings();
        problem = string.Empty;

        if (!double.TryParse(GetValue(document, "font-size"), NumberStyles.Float, CultureInfo.InvariantCulture, out var fontSize) || fontSize < 6 || fontSize > 72)
        {
            problem = "The font size has to be a number between 6 and 72.";
            return false;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = GetValue(document, "headers").Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf(':');
            var name = separator > 0 ? line[..separator].Trim() : string.Empty;

            if (name.Length == 0 || name.Any(m => char.IsWhiteSpace(m) || char.IsControl(m) || m > 127))
            {
                problem = $"Header line {i + 1} has to look like \"Name: value\".";
                return false;
            }

            headers[name] = line[(separator + 1)..].Trim();
        }

        var startPage = GetValue(document, "start-page").Trim();
        var profileDirectory = GetValue(document, "profile-directory").Trim();
        var userAgent = GetValue(document, "user-agent").Trim();
        settings = new BrowserSettings
        {
            StartPage = startPage.Length > 0 ? BrowserEngine.NormalizeAddress(startPage) : BrowserSettings.DefaultStartPage,
            IsScriptingEnabled = IsChecked(document, "scripting"),
            FontSize = fontSize,
            IsPersistent = IsChecked(document, "persistent"),
            ProfileDirectory = profileDirectory.Length > 0 ? profileDirectory : null,
            IsDoNotTrackEnabled = IsChecked(document, "do-not-track"),
            UserAgent = userAgent.Length > 0 ? userAgent : null,
            DefaultHeaders = headers,
            Theme = Themes.FirstOrDefault(m => IsChecked(document, GetThemeId(m))),
        };
        return true;
    }

    private static void ShowStatus(IDocument document, string text, string? kind)
    {
        var status = document.GetElementById("status")!;
        status.TextContent = text;
        status.ClassName = kind is null ? "status" : $"status {kind}";
    }

    private static string GetThemeId(AppTheme theme) => $"theme-{theme.ToString().ToLowerInvariant()}";
}
