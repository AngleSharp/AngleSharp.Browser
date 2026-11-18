namespace AngleSharp.Browser;

using System;

/// <summary>
/// The format of the samples an audio stream plays: 32-bit floats from -1 to 1, the samples
/// of the channels interleaved (e.g. left, right, left, ...).
/// </summary>
/// <param name="SampleRate">The samples per second of every channel, e.g. 44100.</param>
/// <param name="Channels">The number of channels, e.g. 2 for stereo.</param>
public readonly record struct AudioFormat(int SampleRate, int Channels);

/// <summary>
/// Where the audio of the pages plays, e.g. the platform's audio device; see
/// <see cref="BrowserOptions.AudioOutput"/>. The engine opens a stream for every media element
/// playing, which the output pulls the samples of.
/// </summary>
public interface IAudioOutput
{
    /// <summary>
    /// Opens a stream, which pulls its samples from the source once started (on a thread of
    /// the output's choice). The engine disposes the stream once the media stops.
    /// </summary>
    /// <param name="format">The format of the samples.</param>
    /// <param name="source">The source to pull the samples from.</param>
    /// <returns>The stream, which is stopped.</returns>
    IAudioStream Open(AudioFormat format, IAudioSource source);
}

/// <summary>
/// A stream of an <see cref="IAudioOutput"/>.
/// </summary>
public interface IAudioStream : IDisposable
{
    /// <summary>
    /// Starts pulling (and playing) the samples of the source.
    /// </summary>
    void Start();

    /// <summary>
    /// Stops pulling the samples, without waiting for those pulled to have played.
    /// </summary>
    void Stop();
}

/// <summary>
/// The samples of a stream, which the engine provides.
/// </summary>
public interface IAudioSource
{
    /// <summary>
    /// Fills the buffer with the next samples (interleaved, in the stream's format). The
    /// source always fills the whole buffer, with silence where there is nothing to play.
    /// The media's clock advances by what the output pulls, so an output pulls about at the
    /// pace it plays.
    /// </summary>
    /// <param name="buffer">The buffer to fill; its length is a multiple of the channels.</param>
    void Read(Span<float> buffer);
}
