namespace Vision.Models;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

/// <summary>
/// The addresses the user has been to, which the address bar suggests: the most popular (most
/// visited) first, and among equally popular ones the most similar to what has been typed.
/// Kept as JSON next to the other data of a profile, or in memory only.
/// </summary>
public sealed class UrlHistory
{
    /// <summary>
    /// The most addresses kept: beyond it the least popular, longest unvisited ones go.
    /// </summary>
    public const int Capacity = 1000;

    private const string FileName = "history.json";
    // the similarity of longer addresses is about as good as of their beginning
    private const int MaxCompareLength = 128;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly string? _filePath;
    private readonly Lock _lock = new();

    /// <summary>
    /// Creates a history kept in the given directory, or in memory only.
    /// </summary>
    public UrlHistory(string? directory = null)
    {
        _filePath = directory is null ? null : Path.Combine(directory, FileName);
        Load();
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Records a visit of the given address, if it is one worth suggesting.
    /// </summary>
    public void Record(string url, string? title = null)
    {
        if (Normalize(url) is not { } key)
        {
            return;
        }

        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                _entries[key] = entry = new Entry { Url = key };
            }

            entry.Visits++;
            entry.LastVisit = DateTime.UtcNow;

            if (!string.IsNullOrWhiteSpace(title))
            {
                entry.Title = title.Trim();
            }

            Trim();
        }

        Save();
    }

    /// <summary>
    /// Updates the title of an address visited before, which may only be known once it loaded.
    /// </summary>
    public void SetTitle(string url, string? title)
    {
        if (Normalize(url) is not { } key || string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry.Title == title.Trim())
            {
                return;
            }

            entry.Title = title.Trim();
        }

        Save();
    }

    /// <summary>
    /// Finds the addresses matching the text (their address or title contains it), the most
    /// visited first and, if visited as often, the ones most similar to the text.
    /// </summary>
    public IReadOnlyList<UrlSuggestion> Suggest(string? text, int count = 8)
    {
        var query = text?.Trim() ?? string.Empty;

        if (query.Length == 0)
        {
            return [];
        }

        var stripped = Strip(query);
        Entry[] entries;

        lock (_lock)
        {
            entries = [.. _entries.Values];
        }

        return entries
            .Where(m => m.Url.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (m.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .Select(m => (Entry: m, Similarity: GetSimilarity(stripped, Strip(m.Url))))
            .OrderByDescending(m => m.Entry.Visits)
            .ThenByDescending(m => m.Similarity)
            .ThenByDescending(m => m.Entry.LastVisit)
            .ThenBy(m => m.Entry.Url, StringComparer.Ordinal)
            .Take(count)
            .Select(m => new UrlSuggestion(m.Entry.Url, m.Entry.Title ?? string.Empty, m.Entry.Visits))
            .ToList();
    }

    /// <summary>
    /// How similar two addresses are, from 0 (nothing in common) to 1 (the same): one minus
    /// the edits needed to turn one into the other (Levenshtein), relative to the longer one.
    /// </summary>
    public static double GetSimilarity(string a, string b)
    {
        a = a.Length > MaxCompareLength ? a[..MaxCompareLength] : a;
        b = b.Length > MaxCompareLength ? b[..MaxCompareLength] : b;
        var length = Math.Max(a.Length, b.Length);

        if (length == 0)
        {
            return 1;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return 1 - (double)previous[b.Length] / length;
    }

    /// <summary>
    /// The address as the history keeps it (without its fragment), or null for addresses
    /// nobody would type (blank pages, data, scripts).
    /// </summary>
    private static string? Normalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var hash = url.IndexOf('#');
        var key = (hash >= 0 ? url[..hash] : url).Trim();
        var colon = key.IndexOf(':');
        var scheme = colon > 0 ? key[..colon].ToLowerInvariant() : string.Empty;
        return scheme is "about" or "data" or "javascript" or "blob" or "" ? null : key;
    }

    /// <summary>
    /// What is left to compare of an address: users rarely type the scheme or "www.".
    /// </summary>
    private static string Strip(string url)
    {
        var text = url.Trim();

        foreach (var prefix in (string[])["https://", "http://"])
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..];
                break;
            }
        }

        if (text.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            text = text[4..];
        }

        return text.TrimEnd('/');
    }

    /// <summary>
    /// Requires the lock.
    /// </summary>
    private void Trim()
    {
        if (_entries.Count > Capacity)
        {
            foreach (var entry in _entries.Values.OrderBy(m => m.Visits).ThenBy(m => m.LastVisit).Take(_entries.Count - Capacity).ToList())
            {
                _entries.Remove(entry.Url);
            }
        }
    }

    private void Load()
    {
        if (_filePath is null || !File.Exists(_filePath))
        {
            return;
        }

        try
        {
            var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_filePath), JsonOptions) ?? [];

            foreach (var entry in entries)
            {
                if (Normalize(entry.Url) is { } key && entry.Visits > 0)
                {
                    _entries[key] = entry with { Url = key };
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // a broken history must not keep the browser from starting
            Trace.WriteLine($"Loading the history failed: {ex.Message}");
        }
    }

    private void Save()
    {
        if (_filePath is null)
        {
            return;
        }

        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                File.WriteAllText(_filePath, JsonSerializer.Serialize(_entries.Values, JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine($"Saving the history failed: {ex.Message}");
            }
        }
    }

    private sealed record Entry
    {
        public string Url { get; init; } = string.Empty;

        public string? Title { get; set; }

        public int Visits { get; set; }

        public DateTime LastVisit { get; set; }
    }
}

/// <summary>
/// An address the address bar suggests.
/// </summary>
public sealed record UrlSuggestion(string Url, string Title, int Visits)
{
    /// <summary>
    /// Gets what to show first: the title, or the address if there is none.
    /// </summary>
    public string Heading => Title.Length > 0 ? Title : Url;

    /// <summary>
    /// Gets if the address is shown below the title.
    /// </summary>
    public bool HasTitle => Title.Length > 0;
}
