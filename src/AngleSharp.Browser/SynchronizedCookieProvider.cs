namespace AngleSharp.Browser;

using AngleSharp.Dom;
using AngleSharp.Io;

/// <summary>
/// The cookies of a profile, shared by the windows (engines) opened from each other. Each
/// engine runs its own event loop, so the access is serialized.
/// </summary>
sealed class SynchronizedCookieProvider(ICookieProvider provider) : ICookieProvider
{
    private readonly object _lock = new();

    public string GetCookie(Url url)
    {
        lock (_lock)
        {
            return provider.GetCookie(url);
        }
    }

    public void SetCookie(Url url, string value)
    {
        lock (_lock)
        {
            provider.SetCookie(url, value);
        }
    }
}
