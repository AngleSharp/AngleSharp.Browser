namespace AngleSharp.Browser;

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Io;
using AngleSharp.Media;
using AngleSharp.Media.Dom;

/// <summary>
/// Turns the responses of audio elements into their playbacks. The whole response is read
/// first, which the decoders then seek in.
/// </summary>
sealed class AudioResourceService(MediaHost host, IBrowsingContext context) : IResourceService<IAudioInfo>
{
    private const int HeaderLength = 512;

    public bool SupportsType(string mimeType) => host.CanPlayType(mimeType);

    public async Task<IAudioInfo> CreateAsync(IResponse response, CancellationToken cancel)
    {
        var data = new MemoryStream();
        await response.Content.CopyToAsync(data, cancel).ConfigureAwait(false);
        var header = new ReadOnlyMemory<byte>(data.GetBuffer(), 0, (int)Math.Min(data.Length, HeaderLength));
        var codec = host.Codecs.FirstOrDefault(m => m.CanDecode(header.Span)) ??
            throw new NotSupportedException("The audio is of no supported format.");
        data.Position = 0;
        var decoder = codec.Open(data);
        var playback = new AudioPlayback(host, context, decoder);
        host.Add(playback);
        return new AudioInfo(response.Address, playback);
    }

    private sealed class AudioInfo(Url source, IMediaController controller) : IAudioInfo
    {
        public Url Source { get; set; } = source;

        public IMediaController Controller { get; } = controller;
    }
}
