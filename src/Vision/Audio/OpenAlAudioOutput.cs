namespace Vision.Audio;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using AngleSharp.Browser;
using Silk.NET.OpenAL;

/// <summary>
/// Plays the audio of pages on the default device via OpenAL Soft: each stream is a source with
/// a few short buffers, which a thread refills as they were played. All windows share it (one
/// device for the app), so it lives until the app ends.
/// </summary>
sealed unsafe class OpenAlAudioOutput : IAudioOutput
{
    // the media's time is what was pulled, so the buffers ahead of what is heard stay short
    private const int BufferCount = 3;
    private static readonly TimeSpan BufferDuration = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(10);

    private static readonly Lazy<IAudioOutput> s_shared = new(Create);

    private readonly AL _al;
    private readonly Lock _gate = new();
    private readonly List<Stream> _streams = [];

    private OpenAlAudioOutput(AL al)
    {
        _al = al;
        new Thread(Pump) { IsBackground = true, Name = "Audio", Priority = ThreadPriority.AboveNormal }.Start();
    }

    /// <summary>
    /// Gets the output of the app: OpenAL on the default device, or a silent one if there is no
    /// device (e.g. on a server).
    /// </summary>
    public static IAudioOutput Shared => s_shared.Value;

    private static IAudioOutput Create()
    {
        try
        {
            var alc = ALContext.GetApi(soft: true);
            var device = alc.OpenDevice(string.Empty);

            if (device is null)
            {
                Trace.WriteLine("No audio device: pages play silently.");
                return new SilentAudioOutput();
            }

            var context = alc.CreateContext(device, null);

            if (context is null || !alc.MakeContextCurrent(context))
            {
                alc.CloseDevice(device);
                Trace.WriteLine("The audio device could not be used: pages play silently.");
                return new SilentAudioOutput();
            }

            return new OpenAlAudioOutput(AL.GetApi(soft: true));
        }
        catch (Exception ex)
        {
            // e.g. no native library for the platform
            Trace.WriteLine($"OpenAL is not available, pages play silently: {ex.Message}");
            return new SilentAudioOutput();
        }
    }

    public IAudioStream Open(AudioFormat format, IAudioSource source)
    {
        var stream = new Stream(this, format, source);

        lock (_gate)
        {
            _streams.Add(stream);
        }

        return stream;
    }

    private void Pump()
    {
        var streams = new List<Stream>();

        while (true)
        {
            lock (_gate)
            {
                streams.Clear();
                streams.AddRange(_streams);
            }

            foreach (var stream in streams)
            {
                stream.Refill();
            }

            Thread.Sleep(PumpInterval);
        }
    }

    private void Remove(Stream stream)
    {
        lock (_gate)
        {
            _streams.Remove(stream);
        }
    }

    /// <summary>
    /// A source with its buffers. Its lock is never held by the output's (a media opens a stream
    /// while it holds its own lock, which reading from the media takes).
    /// </summary>
    private sealed class Stream : IAudioStream
    {
        private readonly Lock _gate = new();
        private readonly OpenAlAudioOutput _output;
        private readonly IAudioSource _source;
        private readonly int _channels;
        private readonly int _sampleRate;
        private readonly BufferFormat _format;
        private readonly uint _id;
        private readonly uint[] _buffers;
        private readonly float[] _samples;
        private readonly short[] _pcm;
        private bool _isPlaying;
        private bool _isDisposed;

        public Stream(OpenAlAudioOutput output, AudioFormat format, IAudioSource source)
        {
            _output = output;
            _source = source;
            _channels = Math.Max(1, format.Channels);
            _sampleRate = format.SampleRate;
            // more than two channels are played as their first two (front left and right)
            _format = _channels == 1 ? BufferFormat.Mono16 : BufferFormat.Stereo16;
            var frames = (int)(_sampleRate * BufferDuration.TotalSeconds);
            _samples = new float[frames * _channels];
            _pcm = new short[frames * Math.Min(_channels, 2)];
            var al = output._al;
            _id = al.GenSource();
            _buffers = al.GenBuffers(BufferCount);
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_isPlaying || _isDisposed)
                {
                    return;
                }

                foreach (var buffer in _buffers)
                {
                    Fill(buffer);
                }

                _output._al.SourceQueueBuffers(_id, _buffers);
                _output._al.SourcePlay(_id);
                _isPlaying = true;
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (!_isPlaying)
                {
                    return;
                }

                // stopped, all of its buffers count as played
                var al = _output._al;
                al.SourceStop(_id);
                al.GetSourceProperty(_id, GetSourceInteger.BuffersProcessed, out var processed);

                if (processed > 0)
                {
                    al.SourceUnqueueBuffers(_id, new uint[processed]);
                }

                _isPlaying = false;
            }
        }

        public void Dispose()
        {
            Stop();

            lock (_gate)
            {
                if (_isDisposed)
                {
                    return;
                }

                _isDisposed = true;
                _output._al.DeleteSource(_id);
                _output._al.DeleteBuffers(_buffers);
            }

            _output.Remove(this);
        }

        /// <summary>
        /// Gives the buffers played since new samples.
        /// </summary>
        public void Refill()
        {
            lock (_gate)
            {
                if (!_isPlaying)
                {
                    return;
                }

                var al = _output._al;
                al.GetSourceProperty(_id, GetSourceInteger.BuffersProcessed, out var processed);
                var buffer = new uint[1];

                for (var i = 0; i < processed; i++)
                {
                    al.SourceUnqueueBuffers(_id, buffer);
                    Fill(buffer[0]);
                    al.SourceQueueBuffers(_id, buffer);
                }

                // it ran dry (e.g. the app was busy): it goes on with what it has now
                al.GetSourceProperty(_id, GetSourceInteger.SourceState, out var state);

                if (state != (int)SourceState.Playing)
                {
                    al.SourcePlay(_id);
                }
            }
        }

        private void Fill(uint buffer)
        {
            _source.Read(_samples);
            var outputChannels = Math.Min(_channels, 2);

            for (int frame = 0, count = _pcm.Length / outputChannels; frame < count; frame++)
            {
                for (var channel = 0; channel < outputChannels; channel++)
                {
                    var sample = Math.Clamp(_samples[(frame * _channels) + channel], -1f, 1f);
                    _pcm[(frame * outputChannels) + channel] = (short)(sample * short.MaxValue);
                }
            }

            fixed (short* data = _pcm)
            {
                _output._al.BufferData(buffer, _format, data, _pcm.Length * sizeof(short), _sampleRate);
            }
        }
    }
}
