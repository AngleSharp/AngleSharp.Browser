namespace AngleSharp.Browser;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

/// <summary>
/// The media of an engine: where it plays, what may play (the autoplay policy) and which
/// media there is, so the engine stops the media of documents that went away.
/// </summary>
sealed class MediaHost(BrowserOptions options)
{
    private readonly Lock _gate = new();
    // all media of the documents, weakly, as pages create (and drop) media freely
    private readonly List<WeakReference<AudioPlayback>> _media = [];
    // the media playing, which nothing else may hold on to
    private readonly HashSet<AudioPlayback> _playing = [];
    private volatile bool _hasUserActivation;

    public IAudioOutput Output { get; } = options.AudioOutput ?? new SilentAudioOutput();

    public IReadOnlyList<IAudioCodec> Codecs { get; } = [.. options.AudioCodecs];

    /// <summary>
    /// Gets or sets if the user interacted with the current page (a press or a key), which
    /// lets it play audible media (HTML 6.4.3, sticky activation).
    /// </summary>
    public bool HasUserActivation
    {
        get => _hasUserActivation;
        set => _hasUserActivation = value;
    }

    /// <summary>
    /// Gets the media playing.
    /// </summary>
    public int PlayingCount
    {
        get
        {
            lock (_gate)
            {
                return _playing.Count;
            }
        }
    }

    /// <summary>
    /// Raised when a media changed what its controls show other than its time going on (e.g. it
    /// was paused, seeked or muted), from any thread.
    /// </summary>
    public event Action? Changed;

    public void OnChanged() => Changed?.Invoke();

    public bool MayPlay(AudioPlayback playback) =>
        options.IsAutoplayAllowed || _hasUserActivation || playback.IsMuted || playback.Volume == 0;

    /// <summary>
    /// Checks if a content type (with its codecs parameter, if any) plays.
    /// </summary>
    public bool CanPlayType(string type)
    {
        var parameters = type.Split(';');
        var essence = parameters[0].Trim();

        // a response of no audio type (e.g. served as a download) is told by its data
        if (essence.Length == 0 || essence.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase) || essence.Equals("binary/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return parameters.Length == 1 && Codecs.Count > 0;
        }

        var codecs = Codecs.Where(m => m.ContentTypes.Contains(essence, StringComparer.OrdinalIgnoreCase)).ToArray();
        var names = parameters.Skip(1)
            .Select(m => m.Split('=', 2))
            .Where(m => m.Length == 2 && m[0].Trim().Equals("codecs", StringComparison.OrdinalIgnoreCase))
            .SelectMany(m => m[1].Trim().Trim('"').Split(','))
            .Select(m => m.Trim())
            .Where(m => m.Length > 0);
        return codecs.Length > 0 && names.All(name => codecs.Any(codec => codec.CodecNames.Contains(name, StringComparer.OrdinalIgnoreCase)));
    }

    public void Add(AudioPlayback playback)
    {
        lock (_gate)
        {
            _media.RemoveAll(m => !m.TryGetTarget(out _));
            _media.Add(new WeakReference<AudioPlayback>(playback));
        }
    }

    public void OnPlaying(AudioPlayback playback)
    {
        lock (_gate)
        {
            _playing.Add(playback);
        }
    }

    public void OnHalted(AudioPlayback playback)
    {
        lock (_gate)
        {
            _playing.Remove(playback);
        }
    }

    /// <summary>
    /// Stops the media of documents that are no longer shown in their browsing context.
    /// </summary>
    public void StopDetached() => Stop(playback => !ReferenceEquals(playback.Context.Active, playback.Document));

    /// <summary>
    /// Stops all media, e.g. once the engine is disposed.
    /// </summary>
    public void StopAll() => Stop(_ => true);

    private void Stop(Func<AudioPlayback, bool> predicate)
    {
        var stopped = new List<AudioPlayback>();

        lock (_gate)
        {
            foreach (var reference in _media)
            {
                if (reference.TryGetTarget(out var playback) && predicate(playback))
                {
                    stopped.Add(playback);
                }
            }

            _media.RemoveAll(m => !m.TryGetTarget(out var playback) || stopped.Contains(playback));
        }

        foreach (var playback in stopped)
        {
            playback.Stop();
            playback.Dispose();
        }
    }
}
