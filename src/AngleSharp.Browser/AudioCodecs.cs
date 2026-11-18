namespace AngleSharp.Browser;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Concentus;
using Concentus.Oggfile;
using NLayer;
using NVorbis;

/// <summary>
/// A format of audio the engine plays; see <see cref="BrowserOptions.AudioCodecs"/>.
/// </summary>
public interface IAudioCodec
{
    /// <summary>
    /// Gets the content types (without parameters) of the format, e.g. "audio/mpeg".
    /// </summary>
    IReadOnlyCollection<string> ContentTypes { get; }

    /// <summary>
    /// Gets the names of the codecs it decodes, as in the codecs parameter of a content type
    /// (e.g. "vorbis" of "audio/ogg; codecs=vorbis").
    /// </summary>
    IReadOnlyCollection<string> CodecNames { get; }

    /// <summary>
    /// Checks if the data starting with the given bytes (at least the first 64, unless the
    /// data is shorter) is of the format.
    /// </summary>
    /// <param name="header">The first bytes of the data.</param>
    /// <returns>True if the codec decodes the data.</returns>
    bool CanDecode(ReadOnlySpan<byte> header);

    /// <summary>
    /// Opens a decoder for the data, which owns the (seekable) stream.
    /// </summary>
    /// <param name="stream">The data of the format.</param>
    /// <returns>The decoder.</returns>
    IAudioDecoder Open(Stream stream);
}

/// <summary>
/// The decoder of an <see cref="IAudioCodec"/>, giving the samples as 32-bit floats from -1 to
/// 1, the channels interleaved.
/// </summary>
public interface IAudioDecoder : IDisposable
{
    /// <summary>
    /// Gets the samples per second of every channel.
    /// </summary>
    int SampleRate { get; }

    /// <summary>
    /// Gets the number of channels.
    /// </summary>
    int Channels { get; }

    /// <summary>
    /// Gets the duration, if known.
    /// </summary>
    TimeSpan? Duration { get; }

    /// <summary>
    /// Decodes the next samples into the buffer, whose length is a multiple of the channels.
    /// </summary>
    /// <param name="buffer">The buffer to fill.</param>
    /// <returns>The number of samples decoded, fewer than asked for only at the end.</returns>
    int Read(Span<float> buffer);

    /// <summary>
    /// Moves to the given position.
    /// </summary>
    /// <param name="position">The position from the start.</param>
    /// <returns>True if the decoder moved there.</returns>
    bool Seek(TimeSpan position);
}

/// <summary>
/// The audio formats of the engine, which are decoded with managed code only.
/// </summary>
public static class AudioCodecs
{
    /// <summary>
    /// Gets the codec of WAV files (PCM of 8, 16, 24 or 32 bits, or 32-bit floats).
    /// </summary>
    public static IAudioCodec Wav { get; } = new WavCodec();

    /// <summary>
    /// Gets the codec of MP3 files (MPEG audio, decoded by NLayer).
    /// </summary>
    public static IAudioCodec Mp3 { get; } = new Mp3Codec();

    /// <summary>
    /// Gets the codec of Ogg Vorbis files (decoded by NVorbis).
    /// </summary>
    public static IAudioCodec Vorbis { get; } = new VorbisCodec();

    /// <summary>
    /// Gets the codec of Ogg Opus files (decoded by Concentus).
    /// </summary>
    public static IAudioCodec Opus { get; } = new OpusCodec();

    /// <summary>
    /// Creates the list of all codecs of the engine.
    /// </summary>
    /// <returns>The codecs, in the order they are tried.</returns>
    public static List<IAudioCodec> CreateDefault() => [Wav, Mp3, Vorbis, Opus];

    private static bool StartsWith(ReadOnlySpan<byte> data, string text) =>
        data.Length >= text.Length && data[..text.Length].SequenceEqual(Encoding.ASCII.GetBytes(text));

    private static bool IsOgg(ReadOnlySpan<byte> header, string codecSignature) =>
        StartsWith(header, "OggS") && header.IndexOf(Encoding.ASCII.GetBytes(codecSignature)) >= 0;

    private sealed class WavCodec : IAudioCodec
    {
        public IReadOnlyCollection<string> ContentTypes { get; } = ["audio/wav", "audio/x-wav", "audio/wave", "audio/vnd.wave", "audio/x-pn-wav"];

        public IReadOnlyCollection<string> CodecNames { get; } = ["1", "3"];

        public bool CanDecode(ReadOnlySpan<byte> header) =>
            header.Length >= 12 && StartsWith(header, "RIFF") && StartsWith(header[8..], "WAVE");

        public IAudioDecoder Open(Stream stream) => new WavDecoder(stream);
    }

    private sealed class Mp3Codec : IAudioCodec
    {
        public IReadOnlyCollection<string> ContentTypes { get; } = ["audio/mpeg", "audio/mp3", "audio/mpeg3", "audio/x-mpeg", "audio/x-mp3"];

        public IReadOnlyCollection<string> CodecNames { get; } = ["mp3", "mp4a.6b", "mp4a.69", "mp4a.40.34"];

        // an ID3 tag or the sync of an MPEG audio frame (AAC's ADTS has layer 0)
        public bool CanDecode(ReadOnlySpan<byte> header) =>
            StartsWith(header, "ID3") || header.Length >= 2 && header[0] == 0xFF && (header[1] & 0xE0) == 0xE0 && (header[1] & 0x06) != 0;

        public IAudioDecoder Open(Stream stream) => new Mp3Decoder(stream);
    }

    private sealed class VorbisCodec : IAudioCodec
    {
        public IReadOnlyCollection<string> ContentTypes { get; } = ["audio/ogg", "application/ogg", "audio/vorbis"];

        public IReadOnlyCollection<string> CodecNames { get; } = ["vorbis"];

        public bool CanDecode(ReadOnlySpan<byte> header) => IsOgg(header, "\u0001vorbis");

        public IAudioDecoder Open(Stream stream) => new VorbisDecoder(stream);
    }

    private sealed class OpusCodec : IAudioCodec
    {
        public IReadOnlyCollection<string> ContentTypes { get; } = ["audio/ogg", "application/ogg", "audio/opus"];

        public IReadOnlyCollection<string> CodecNames { get; } = ["opus"];

        public bool CanDecode(ReadOnlySpan<byte> header) => IsOgg(header, "OpusHead");

        public IAudioDecoder Open(Stream stream) => new OpusDecoder(stream);
    }

    private sealed class WavDecoder : IAudioDecoder
    {
        private const int Pcm = 1;
        private const int Float = 3;
        private const int Extensible = 0xFFFE;

        private readonly Stream _stream;
        private readonly int _format;
        private readonly int _bits;
        private readonly int _blockAlign;
        private readonly long _dataStart;
        private readonly long _dataLength;
        private byte[] _buffer = [];
        private long _position;

        public WavDecoder(Stream stream)
        {
            _stream = stream;
            var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            stream.Position = 12;
            var hasFormat = false;

            while (stream.Position + 8 <= stream.Length)
            {
                var id = Encoding.ASCII.GetString(reader.ReadBytes(4));
                var size = reader.ReadUInt32();
                var next = stream.Position + size + (size & 1);

                if (id == "fmt ")
                {
                    _format = reader.ReadUInt16();
                    Channels = reader.ReadUInt16();
                    SampleRate = (int)reader.ReadUInt32();
                    reader.ReadUInt32();
                    _blockAlign = reader.ReadUInt16();
                    _bits = reader.ReadUInt16();

                    if (_format == Extensible && size >= 26)
                    {
                        reader.ReadUInt16();
                        reader.ReadUInt16();
                        reader.ReadUInt32();
                        // the sub format's GUID starts with the format's code
                        _format = reader.ReadUInt16();
                    }

                    hasFormat = true;
                }
                else if (id == "data")
                {
                    _dataStart = stream.Position;
                    _dataLength = Math.Min(size, stream.Length - _dataStart);
                    break;
                }

                stream.Position = next;
            }

            var isSupported = _format == Pcm && _bits is 8 or 16 or 24 or 32 || _format == Float && _bits == 32;

            if (!hasFormat || _dataStart == 0 || !isSupported || Channels <= 0 || SampleRate <= 0 || _blockAlign != Channels * _bits / 8)
            {
                throw new InvalidDataException("The WAV file has no supported format.");
            }
        }

        public int SampleRate { get; }

        public int Channels { get; }

        public TimeSpan? Duration => TimeSpan.FromSeconds((double)(_dataLength / _blockAlign) / SampleRate);

        public int Read(Span<float> buffer)
        {
            var bytesPerSample = _bits / 8;
            var frames = (int)Math.Min(buffer.Length / Channels, (_dataLength - _position) / _blockAlign);
            var length = frames * _blockAlign;

            if (frames <= 0)
            {
                return 0;
            }

            if (_buffer.Length < length)
            {
                _buffer = new byte[length];
            }

            _stream.Position = _dataStart + _position;
            _stream.ReadExactly(_buffer, 0, length);
            _position += length;
            var data = _buffer.AsSpan(0, length);
            var count = frames * Channels;

            for (var i = 0; i < count; i++)
            {
                var sample = data.Slice(i * bytesPerSample, bytesPerSample);
                buffer[i] = _format == Float ? BinaryPrimitives.ReadSingleLittleEndian(sample) : _bits switch
                {
                    8 => (sample[0] - 128) / 128f,
                    16 => BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768f,
                    24 => (sample[0] | sample[1] << 8 | (sbyte)sample[2] << 16) / 8388608f,
                    _ => BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648f,
                };
            }

            return count;
        }

        public bool Seek(TimeSpan position)
        {
            var frame = (long)(Math.Max(0, position.TotalSeconds) * SampleRate);
            _position = Math.Min(frame * _blockAlign, _dataLength / _blockAlign * _blockAlign);
            return true;
        }

        public void Dispose() => _stream.Dispose();
    }

    private sealed class Mp3Decoder(Stream stream) : IAudioDecoder
    {
        private readonly MpegFile _file = new(stream);

        public int SampleRate => _file.SampleRate;

        public int Channels => _file.Channels;

        public TimeSpan? Duration => _file.Duration > TimeSpan.Zero ? _file.Duration : null;

        public int Read(Span<float> buffer) => ReadFully(buffer, _file.ReadSamples);

        public bool Seek(TimeSpan position)
        {
            if (!_file.CanSeek)
            {
                return false;
            }

            _file.Time = position;
            return true;
        }

        public void Dispose()
        {
            _file.Dispose();
            stream.Dispose();
        }
    }

    private sealed class VorbisDecoder(Stream stream) : IAudioDecoder
    {
        private readonly VorbisReader _reader = new(stream, closeOnDispose: true);

        public int SampleRate => _reader.SampleRate;

        public int Channels => _reader.Channels;

        public TimeSpan? Duration => _reader.TotalTime > TimeSpan.Zero ? _reader.TotalTime : null;

        public int Read(Span<float> buffer) => ReadFully(buffer, _reader.ReadSamples);

        public bool Seek(TimeSpan position)
        {
            _reader.SeekTo(position, SeekOrigin.Begin);
            return true;
        }

        public void Dispose() => _reader.Dispose();
    }

    private sealed class OpusDecoder : IAudioDecoder
    {
        // Opus always decodes at 48 kHz
        private const int Rate = 48000;

        private readonly Stream _stream;
        private OpusOggReadStream _reader;
        private short[] _pending = [];
        private int _offset;

        public OpusDecoder(Stream stream)
        {
            _stream = stream;
            Channels = ReadChannels(stream);
            _reader = OpenReader();
            Duration = _reader.TotalTime > TimeSpan.Zero ? _reader.TotalTime : null;
        }

        public int SampleRate => Rate;

        public int Channels { get; }

        public TimeSpan? Duration { get; }

        public int Read(Span<float> buffer)
        {
            var count = 0;

            while (count < buffer.Length && Fill())
            {
                var length = Math.Min(buffer.Length - count, _pending.Length - _offset);

                for (var i = 0; i < length; i++)
                {
                    buffer[count + i] = _pending[_offset + i] / 32768f;
                }

                count += length;
                _offset += length;
            }

            return count;
        }

        // the reader of Concentus has no packets after seeking back, so it starts over and
        // decodes up to the position (which is cheap for Opus)
        public bool Seek(TimeSpan position)
        {
            _reader = OpenReader();
            _pending = [];
            _offset = 0;
            var skip = (long)(Math.Max(0, position.TotalSeconds) * Rate) * Channels;

            while (skip > 0 && Fill())
            {
                var length = (int)Math.Min(skip, _pending.Length - _offset);
                _offset += length;
                skip -= length;
            }

            return true;
        }

        public void Dispose() => _stream.Dispose();

        private OpusOggReadStream OpenReader()
        {
            _stream.Position = 0;
            return new OpusOggReadStream(OpusCodecFactory.CreateDecoder(Rate, Channels), _stream);
        }

        // makes sure there are pending samples, unless at the end
        private bool Fill()
        {
            while (_offset >= _pending.Length)
            {
                if (!_reader.HasNextPacket || _reader.DecodeNextPacket() is not { } packet)
                {
                    return false;
                }

                _pending = packet;
                _offset = 0;
            }

            return true;
        }

        // the channels are in the identification header, the first packet (RFC 7845, 5.1)
        private static int ReadChannels(Stream stream)
        {
            var header = new byte[Math.Min(stream.Length, 512)];
            stream.Position = 0;
            stream.ReadExactly(header);
            var index = header.AsSpan().IndexOf("OpusHead"u8);

            if (index < 0 || index + 9 >= header.Length || header[index + 9] is 0 or > 2)
            {
                throw new InvalidDataException("The Ogg Opus file has no supported channels.");
            }

            return header[index + 9];
        }
    }

    private delegate int SampleReader(Span<float> buffer);

    // decoders may give fewer samples than asked for before the end (e.g. at a frame's end)
    private static int ReadFully(Span<float> buffer, SampleReader read)
    {
        var count = 0;

        while (count < buffer.Length)
        {
            var length = read(buffer[count..]);

            if (length <= 0)
            {
                break;
            }

            count += length;
        }

        return count;
    }
}
