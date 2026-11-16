namespace AngleSharp.Browser;

using System;
using System.Diagnostics;
using System.Threading;

/// <summary>
/// The audio output without a device: it pulls the samples in real time and drops them, so
/// media plays (with its time advancing, events and its end) without being heard. The engine
/// uses it unless <see cref="BrowserOptions.AudioOutput"/> gives another one.
/// </summary>
public sealed class SilentAudioOutput : IAudioOutput
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(20);

    /// <inheritdoc />
    public IAudioStream Open(AudioFormat format, IAudioSource source) => new SilentStream(format, source);

    private sealed class SilentStream(AudioFormat format, IAudioSource source) : IAudioStream
    {
        private readonly Lock _gate = new();
        private readonly Stopwatch _clock = new();
        private float[] _buffer = [];
        private Timer? _timer;
        private long _frames;

        public void Start()
        {
            lock (_gate)
            {
                if (_timer is null)
                {
                    _frames = 0;
                    _clock.Restart();
                    _timer = new Timer(_ => Pull(), null, Interval, Interval);
                }
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                _timer?.Dispose();
                _timer = null;
                _clock.Stop();
            }
        }

        public void Dispose() => Stop();

        private void Pull()
        {
            lock (_gate)
            {
                if (_timer is null)
                {
                    return;
                }

                // what played since the start, so the clock doesn't drift with the timer
                var due = (long)(_clock.Elapsed.TotalSeconds * format.SampleRate) - _frames;

                if (due <= 0)
                {
                    return;
                }

                var length = (int)Math.Min(due, format.SampleRate) * format.Channels;

                if (_buffer.Length < length)
                {
                    _buffer = new float[length];
                }

                source.Read(_buffer.AsSpan(0, length));
                _frames += length / format.Channels;
            }
        }
    }
}
