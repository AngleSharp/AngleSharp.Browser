namespace AngleSharp.Browser;

using System;
using System.Threading;
using AngleSharp.Dom;
using AngleSharp.Dom.Events;
using AngleSharp.Media.Dom;

/// <summary>
/// The controller of an audio resource: it decodes the samples as the output pulls them, so
/// its clock is what the output played. Its events are raised on the event loop of its
/// document; the element fires those of the state it changes itself (play, pause, ...).
/// </summary>
sealed class AudioPlayback : IMediaController, IAudioSource
{
    private static readonly TimeSpan TimeUpdateInterval = TimeSpan.FromMilliseconds(250);

    private readonly Lock _gate = new();
    private readonly MediaHost _host;
    private readonly IBrowsingContext _context;
    private readonly IAudioDecoder _decoder;
    private readonly int _channels;
    private readonly int _sampleRate;
    private IAudioStream? _stream;
    private Timer? _timeUpdates;
    private MediaControllerPlaybackState _state;
    private long _frames;
    private double _volume = 1.0;
    private bool _isMuted;
    private bool _isDisposed;

    public AudioPlayback(MediaHost host, IBrowsingContext context, IAudioDecoder decoder)
    {
        _host = host;
        _context = context;
        _decoder = decoder;
        _channels = decoder.Channels;
        _sampleRate = decoder.SampleRate;
        Document = context.Active;
    }

    /// <summary>
    /// Gets the document the media plays for.
    /// </summary>
    public IDocument? Document { get; }

    /// <summary>
    /// Gets the browsing context of the document.
    /// </summary>
    public IBrowsingContext Context => _context;

    public ITimeRanges? BufferedTime => Seekable;

    public ITimeRanges? SeekableTime => Seekable;

    public ITimeRanges? PlayedTime => new TimeRanges(0, CurrentTime);

    public double Duration => _decoder.Duration?.TotalSeconds ?? double.NaN;

    public double CurrentTime
    {
        get
        {
            lock (_gate)
            {
                return (double)_frames / _sampleRate;
            }
        }
        set
        {
            lock (_gate)
            {
                var time = Math.Max(0, double.IsNaN(Duration) ? value : Math.Min(value, Duration));

                if (_decoder.Seek(TimeSpan.FromSeconds(time)))
                {
                    _frames = (long)(time * _sampleRate);

                    if (_state == MediaControllerPlaybackState.Ended)
                    {
                        _state = MediaControllerPlaybackState.Waiting;
                    }
                }
            }

            _host.OnChanged();
        }
    }

    public double DefaultPlaybackRate { get; set; } = 1.0;

    // TODO: other rates than 1 (the samples would need to be resampled)
    public double PlaybackRate { get; set; } = 1.0;

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0.0, 1.0);
            _host.OnChanged();
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            _isMuted = value;
            _host.OnChanged();
        }
    }

    public bool IsPaused => _state != MediaControllerPlaybackState.Playing;

    public MediaReadyState ReadyState => MediaReadyState.EnoughData;

    public MediaControllerPlaybackState PlaybackState => _state;

    public bool IsPlaying => _state == MediaControllerPlaybackState.Playing;

    private TimeRanges? Seekable => double.IsNaN(Duration) ? null : new TimeRanges(0, Duration);

    // the element announces what the playback has from the start (all of its data)
#pragma warning disable CS0067
    public event DomEventHandler? Emptied;
    public event DomEventHandler? LoadedMetadata;
    public event DomEventHandler? LoadedData;
    public event DomEventHandler? CanPlay;
    public event DomEventHandler? CanPlayThrough;
    public event DomEventHandler? Ended;
    public event DomEventHandler? Waiting;
    public event DomEventHandler? DurationChanged;
    public event DomEventHandler? TimeUpdated;
    public event DomEventHandler? Paused;
    public event DomEventHandler? Played;
    public event DomEventHandler? Playing;
    public event DomEventHandler? RateChanged;
    public event DomEventHandler? VolumeChanged;
#pragma warning restore CS0067

    public void Play()
    {
        IAudioStream? stream;

        lock (_gate)
        {
            if (_isDisposed)
            {
                throw new DomException(DomError.InvalidState);
            }

            if (_state == MediaControllerPlaybackState.Playing)
            {
                return;
            }

            if (!_host.MayPlay(this))
            {
                throw new DomException("NotAllowedError");
            }

            if (_state == MediaControllerPlaybackState.Ended)
            {
                _decoder.Seek(TimeSpan.Zero);
                _frames = 0;
            }

            _state = MediaControllerPlaybackState.Playing;
            stream = _stream = _host.Output.Open(new AudioFormat(_sampleRate, _channels), this);
            _timeUpdates = new Timer(_ => Raise(TimeUpdated, EventNames.TimeUpdate), null, TimeUpdateInterval, TimeUpdateInterval);
        }

        _host.OnPlaying(this);
        // outside of the lock: an output may pull right away (on another thread)
        stream.Start();
        _host.OnChanged();
        Raise(Playing, EventNames.Playing);
    }

    public void Pause() => Halt(MediaControllerPlaybackState.Waiting);

    /// <summary>
    /// Stops the media for the engine (e.g. as its document went away), which the element
    /// hears as a pause.
    /// </summary>
    public void Stop()
    {
        if (Halt(MediaControllerPlaybackState.Waiting))
        {
            Raise(Paused, EventNames.Pause);
        }
    }

    /// <summary>
    /// Stops the media for good and frees its decoder.
    /// </summary>
    public void Dispose()
    {
        Halt(MediaControllerPlaybackState.Waiting);

        lock (_gate)
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                _decoder.Dispose();
            }
        }
    }

    public void Read(Span<float> buffer)
    {
        var hasEnded = false;

        lock (_gate)
        {
            var count = 0;

            if (_state == MediaControllerPlaybackState.Playing && !_isDisposed)
            {
                count = _decoder.Read(buffer);
                count -= count % _channels;
                var gain = _isMuted ? 0f : (float)_volume;

                if (gain != 1f)
                {
                    for (var i = 0; i < count; i++)
                    {
                        buffer[i] *= gain;
                    }
                }

                _frames += count / _channels;
                hasEnded = count < buffer.Length;
            }

            buffer[count..].Clear();
        }

        if (hasEnded)
        {
            // not on the output's thread, which the stream is stopped from
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (Halt(MediaControllerPlaybackState.Ended))
                {
                    Raise(TimeUpdated, EventNames.TimeUpdate);
                    Raise(Ended, EventNames.Ended);
                }
            });
        }
    }

    /// <summary>
    /// Stops playing (if it does) and goes to the given state.
    /// </summary>
    private bool Halt(MediaControllerPlaybackState state)
    {
        IAudioStream? stream;
        Timer? timeUpdates;

        lock (_gate)
        {
            if (_state != MediaControllerPlaybackState.Playing)
            {
                return false;
            }

            _state = state;
            stream = _stream;
            timeUpdates = _timeUpdates;
            _stream = null;
            _timeUpdates = null;
        }

        // outside of the lock, as an output may wait for a pull in progress
        timeUpdates?.Dispose();
        stream?.Stop();
        stream?.Dispose();
        _host.OnHalted(this);
        _host.OnChanged();
        return true;
    }

    private void Raise(DomEventHandler? handler, string name)
    {
        if (handler is not null)
        {
            _context.GetService<IEventLoop>().Enqueue(() => handler.Invoke(this, new Event(name)));
        }
    }

    private sealed class TimeRanges(double start, double end) : ITimeRanges
    {
        public int Length => end > start ? 1 : 0;

        public double Start(int index) => index == 0 && Length > 0 ? start : throw new DomException(DomError.IndexSizeError);

        public double End(int index) => index == 0 && Length > 0 ? end : throw new DomException(DomError.IndexSizeError);
    }
}
