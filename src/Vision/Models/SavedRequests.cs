namespace Vision.Models;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

/// <summary>
/// The requests the user saved in the requester (vision:requester) under a name, to send them
/// again later. Kept as JSON next to the other data of a profile, or in memory only, and
/// shared by the windows of a profile.
/// </summary>
public sealed class SavedRequests
{
    private const string FileName = "requests.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly List<SavedRequest> _requests = [];
    private readonly string? _filePath;
    private readonly Lock _lock = new();

    /// <summary>
    /// Creates the saved requests kept in the given directory, or in memory only.
    /// </summary>
    public SavedRequests(string? directory = null)
    {
        _filePath = directory is null ? null : Path.Combine(directory, FileName);
        Load();
    }

    /// <summary>
    /// Raised (on the thread changing them) when requests were saved or removed.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Gets the saved requests, the most recently saved first.
    /// </summary>
    public IReadOnlyList<SavedRequest> All
    {
        get
        {
            lock (_lock)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>
    /// Finds the request of the given name (ignoring case).
    /// </summary>
    public SavedRequest? Find(string name)
    {
        lock (_lock)
        {
            return _requests.Find(m => IsNamed(m, name));
        }
    }

    /// <summary>
    /// Saves the request, replacing the one of the same name (ignoring case), if any.
    /// </summary>
    public void Save(SavedRequest request)
    {
        lock (_lock)
        {
            _requests.RemoveAll(m => IsNamed(m, request.Name));
            _requests.Insert(0, request);
        }

        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Removes the request of the given name, if there is one.
    /// </summary>
    public bool Remove(string name)
    {
        lock (_lock)
        {
            if (_requests.RemoveAll(m => IsNamed(m, name)) == 0)
            {
                return false;
            }
        }

        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static bool IsNamed(SavedRequest request, string name) =>
        string.Equals(request.Name, name.Trim(), StringComparison.OrdinalIgnoreCase);

    private void Load()
    {
        if (_filePath is null || !File.Exists(_filePath))
        {
            return;
        }

        try
        {
            var requests = JsonSerializer.Deserialize<List<SavedRequest>>(File.ReadAllText(_filePath), JsonOptions) ?? [];
            _requests.AddRange(requests
                .Where(m => !string.IsNullOrWhiteSpace(m.Name) && m.Url is not null)
                .DistinctBy(m => m.Name.Trim(), StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // broken saved requests must not keep the browser from starting
            Trace.WriteLine($"Loading the saved requests failed: {ex.Message}");
        }
    }

    private void Persist()
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
                File.WriteAllText(_filePath, JsonSerializer.Serialize(_requests, JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine($"Saving the requests failed: {ex.Message}");
            }
        }
    }
}

/// <summary>
/// A request saved in the requester: what its form showed when it was saved.
/// </summary>
/// <param name="Name">The name the user gave it.</param>
/// <param name="Method">The method, e.g., "Get".</param>
/// <param name="Url">The address as typed.</param>
/// <param name="Headers">The headers, one "Name: value" per line.</param>
/// <param name="Body">The body sent, if any.</param>
/// <param name="Requester">The name of the requester to use, or null for the automatic choice.</param>
/// <param name="UseCookies">If the cookies of the browser are sent and kept.</param>
public sealed record SavedRequest(string Name, string Method, string Url, string Headers, string Body, string? Requester, bool UseCookies);
