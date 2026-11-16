namespace AngleSharp.Browser.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Html.Dom;
using AngleSharp.Io;
using NUnit.Framework;

/// <summary>
/// Audio: the decoders, the media elements of pages, where they play and what may play.
/// </summary>
[TestFixture]
public class AudioTests
{
    private const string Address = "https://example.com/";
    private const double ToneDuration = 0.25;

    [TestCase("tone.wav", 22050, 1)]
    [TestCase("tone.mp3", 22050, 1)]
    [TestCase("tone.ogg", 22050, 1)]
    [TestCase("tone.opus", 48000, 2)]
    public void CodecsDecodeTheirFormat(string name, int sampleRate, int channels)
    {
        var data = File.ReadAllBytes(GetMediaPath(name));
        var codecs = AudioCodecs.CreateDefault().Where(m => m.CanDecode(data)).ToArray();

        Assert.That(codecs, Has.Length.EqualTo(1));

        using var decoder = codecs[0].Open(new MemoryStream(data));
        var samples = ReadAll(decoder);
        var seconds = (double)samples.Length / decoder.Channels / decoder.SampleRate;

        Assert.That(decoder.SampleRate, Is.EqualTo(sampleRate));
        Assert.That(decoder.Channels, Is.EqualTo(channels));
        Assert.That(decoder.Duration?.TotalSeconds, Is.EqualTo(ToneDuration).Within(0.08));
        // encoders pad the start and the end a bit (MP3 to whole frames of 1152 samples)
        Assert.That(seconds, Is.EqualTo(ToneDuration).Within(0.08));
        Assert.That(samples.Max(Math.Abs), Is.GreaterThan(0.08f));

        // seeking back gives the samples again
        Assert.That(decoder.Seek(TimeSpan.Zero), Is.True);
        Assert.That(ReadAll(decoder).Length, Is.EqualTo(samples.Length).Within(samples.Length / 10));
    }

    [Test]
    public void WavCodecDecodesTheSampleFormats()
    {
        foreach (var (bits, isFloat) in new[] { (8, false), (16, false), (24, false), (32, false), (32, true) })
        {
            var data = CreateWav(8000, 1, 0.1, bits, isFloat, 0.5);
            using var decoder = AudioCodecs.Wav.Open(new MemoryStream(data));
            var samples = ReadAll(decoder);

            Assert.That(samples, Has.Length.EqualTo(800), $"{bits} bits");
            Assert.That(samples[0], Is.EqualTo(0.5f).Within(0.01f), $"{bits} bits");
        }
    }

    [Test]
    public async Task CanPlayTypeAnswersByTheCodecs()
    {
        using var engine = CreateEngine();
        await engine.LoadHtmlAsync("<title>Audio</title>", Address);

        var answers = await engine.EvaluateScriptAsync("""
            ['audio/mpeg', 'audio/wav', 'audio/ogg; codecs="vorbis"', 'audio/ogg; codecs=opus', 'audio/mp4', 'audio/ogg; codecs=flac', 'video/mp4']
                .map(function (type) { return new Audio().canPlayType(type); }).join(',')
            """);

        Assert.That(answers, Is.EqualTo("maybe,maybe,maybe,maybe,,,"));
    }

    [TestCase("tone.wav")]
    [TestCase("tone.mp3")]
    [TestCase("tone.ogg")]
    [TestCase("tone.opus")]
    public async Task ScriptsPlayAudioToItsEnd(string name)
    {
        var output = new RecordingOutput();
        using var engine = CreateEngine(output, allowAutoplay: true);
        await engine.LoadHtmlAsync("<title>Audio</title>", Address);

        await engine.EvaluateScriptAsync($$"""
            window.events = [];
            var audio = new Audio('app:{{name}}');
            ['play', 'playing', 'pause', 'ended', 'loadedmetadata', 'canplaythrough'].forEach(function (type) {
                audio.addEventListener(type, function () { events.push(type); });
            });
            audio.play().then(function () { events.push('resolved'); });
            """);
        await Eventually(async () => (string?)await engine.EvaluateScriptAsync("events.join(',')") is { } events && events.EndsWith("ended"));

        // the resource of the scheme is there at once, so it may be ready before the play event
        var events = ((string?)await engine.EvaluateScriptAsync("events.join(',')"))!.Split(',');
        Assert.That(events, Is.EquivalentTo(new[] { "play", "loadedmetadata", "canplaythrough", "playing", "resolved", "pause", "ended" }));
        Assert.That(events[^5..], Is.EqualTo(new[] { "play", "playing", "resolved", "pause", "ended" }).Or.EqualTo(new[] { "canplaythrough", "playing", "resolved", "pause", "ended" }));
        Assert.That(Array.IndexOf(events, "play"), Is.LessThan(Array.IndexOf(events, "playing")));
        Assert.That(await engine.EvaluateScriptAsync("audio.paused && audio.ended"), Is.EqualTo(true));
        // TODO: MP3 keeps the delay and padding of its encoder, which the LAME header tells
        Assert.That((double)(await engine.EvaluateScriptAsync("audio.currentTime"))!, Is.EqualTo(ToneDuration).Within(0.08));
        Assert.That(output.Peak, Is.GreaterThan(0.08f));
        Assert.That(output.OpenStreams, Is.EqualTo(0));
    }

    [Test]
    public async Task VolumeAndMutedScaleTheSamples()
    {
        var output = new RecordingOutput();
        using var engine = CreateEngine(output, allowAutoplay: true);
        await engine.LoadHtmlAsync("<title>Audio</title>", Address);

        await engine.EvaluateScriptAsync("var audio = new Audio('app:tone.wav'); audio.volume = 0.25; audio.onended = function () { document.title = 'ended'; }; audio.play();");
        await Eventually(() => engine.Title == "ended");

        // the tone peaks at 1/8
        Assert.That(output.Peak, Is.EqualTo(0.125f / 4).Within(0.005f));
        Assert.That(output.Formats, Is.EqualTo(new[] { new AudioFormat(22050, 1) }));

        output.Reset();
        await engine.EvaluateScriptAsync("audio.muted = true; document.title = ''; audio.play();");
        await Eventually(() => engine.Title == "ended");

        Assert.That(output.Peak, Is.EqualTo(0f));
    }

    [Test]
    public async Task AudibleMediaWaitsForTheUserToInteract()
    {
        using var engine = CreateEngine();
        await engine.LoadHtmlAsync("<title>Audio</title><div style='height:100px'></div>", Address);

        await engine.EvaluateScriptAsync("""
            var audio = new Audio('app:tone.wav');
            audio.oncanplaythrough = function () {
                audio.play().then(function () { document.title = 'played'; }, function (e) { document.title = e.name; });
            };
            """);
        await Eventually(() => engine.Title == "NotAllowedError");

        Assert.That(await engine.EvaluateScriptAsync("audio.paused"), Is.EqualTo(true));

        // muted media plays anyway
        await engine.EvaluateScriptAsync("var muted = new Audio('app:tone.wav'); muted.muted = true; muted.play().then(function () { document.title = 'muted'; });");
        await Eventually(() => engine.Title == "muted");

        // a press activates the page
        await engine.PointerDownAsync(10, 10);
        await engine.PointerUpAsync(10, 10);
        await engine.EvaluateScriptAsync("audio.play().then(function () { document.title = 'played'; });");
        await Eventually(() => engine.Title == "played");
    }

    [Test]
    public async Task AutoplayElementsOfTheDocumentPlay()
    {
        var output = new RecordingOutput();
        using var engine = CreateEngine(output, allowAutoplay: true);
        await engine.LoadHtmlAsync("<title>Audio</title><audio autoplay loop src='app:long.wav'></audio>", Address);

        await Eventually(() => output.OpenStreams == 1);

        var audio = await engine.InvokeAsync(document => (IHtmlAudioElement)document!.QuerySelector("audio")!);
        Assert.That(await engine.InvokeAsync(_ => audio.IsPaused), Is.False);
    }

    [Test]
    public async Task MediaStopsOnceItsDocumentIsLeft()
    {
        var output = new RecordingOutput();
        using var engine = CreateEngine(output, allowAutoplay: true);
        await engine.LoadHtmlAsync("<title>Audio</title>", Address);

        await engine.EvaluateScriptAsync("var audio = new Audio('app:long.wav'); audio.play();");
        await Eventually(() => output.OpenStreams == 1);

        await engine.LoadHtmlAsync("<title>Other</title>", Address + "other");
        await Eventually(() => output.OpenStreams == 0);
    }

    [Test]
    public async Task LoopingMediaPlaysAgain()
    {
        var output = new RecordingOutput();
        using var engine = CreateEngine(output, allowAutoplay: true);
        await engine.LoadHtmlAsync("<title>Audio</title>", Address);

        await engine.EvaluateScriptAsync("var audio = new Audio('app:tone.wav'); audio.loop = true; audio.play();");
        await Eventually(() => output.Formats.Count >= 2);

        Assert.That(await engine.EvaluateScriptAsync("audio.paused"), Is.EqualTo(false));
        await engine.EvaluateScriptAsync("audio.pause()");
        await Eventually(() => output.OpenStreams == 0);
    }

    [Test]
    public async Task UnsupportedAudioGivesAnError()
    {
        using var engine = CreateEngine(allowAutoplay: true);
        await engine.LoadHtmlAsync("<title>Audio</title>", Address);

        await engine.EvaluateScriptAsync("""
            var audio = new Audio('app:tone.m4a');
            audio.onerror = function () { document.title = 'error ' + audio.error.code; };
            audio.play().catch(function (e) { window.rejection = e.name; });
            """);
        await Eventually(() => engine.Title == "error 4");

        Assert.That(await engine.EvaluateScriptAsync("window.rejection"), Is.EqualTo("NotSupportedError"));
    }

    [Test]
    public async Task ControlsPlayPauseAndMute()
    {
        var output = new RecordingOutput();
        using var engine = CreateEngine(output);
        await LoadControlsAsync(engine);

        // the play button (a press also lets the page play)
        await engine.ClickAsync(28, 27);
        await Eventually(() => output.OpenStreams == 1);
        Assert.That(await engine.EvaluateScriptAsync("audio.paused"), Is.EqualTo(false));

        // the mute button, then the pause button
        await engine.ClickAsync(272, 27);
        Assert.That(await engine.EvaluateScriptAsync("audio.muted"), Is.EqualTo(true));
        await engine.ClickAsync(28, 27);
        await Eventually(() => output.OpenStreams == 0);
        Assert.That(await engine.EvaluateScriptAsync("audio.paused"), Is.EqualTo(true));
        Assert.That(await engine.EvaluateScriptAsync("events.join(',')"), Is.EqualTo("play,playing,volumechange,pause"));
    }

    [Test]
    public async Task ControlsSeekWhileTheTimelineIsDragged()
    {
        using var engine = CreateEngine();
        await LoadControlsAsync(engine);

        // from the start of the timeline to its end, along a pixel row of it
        await engine.PointerDownAsync(150, 27);
        var pressed = (double)(await engine.EvaluateScriptAsync("audio.currentTime"))!;
        engine.MovePointer(299, 27);
        await Eventually(async () => (double)(await engine.EvaluateScriptAsync("audio.currentTime"))! == 30);
        await engine.PointerUpAsync(299, 27);

        Assert.That(pressed, Is.GreaterThan(0).And.LessThan(30));
        // released, the pointer moves without seeking
        engine.MovePointer(150, 27);
        await engine.EvaluateScriptAsync("0");
        Assert.That(await engine.EvaluateScriptAsync("audio.currentTime"), Is.EqualTo(30));
    }

    [Test]
    public async Task PlayingControlsMoveOnWithoutALayout()
    {
        using var engine = CreateEngine(allowAutoplay: true);
        await LoadControlsAsync(engine);
        await engine.EvaluateScriptAsync("audio.play()");
        await Eventually(async () => await engine.EvaluateScriptAsync("audio.paused") is false);

        // the same frame rasterized later shows the time gone on
        var frame = (await engine.PrepareFrameAsync())!;
        var before = frame.Rasterize().Data.ToArray();
        await Task.Delay(1200);
        var after = frame.Rasterize().Data;

        Assert.That(after.SequenceEqual(before), Is.False);
    }

    [Test]
    public async Task MediaWithoutControlsPlaysWithoutALayout()
    {
        using var engine = CreateEngine(allowAutoplay: true);
        await engine.LoadHtmlAsync("<title>Audio</title><p>A game playing its sounds</p>", Address);
        (await engine.PrepareFrameAsync())!.Rasterize();

        // like a game rewinding and playing its sound effects
        await engine.EvaluateScriptAsync("""
            var audio = new Audio('app:tone.wav'), plays = 0;
            audio.onended = function () { if (++plays < 2) { audio.currentTime = 0; audio.play(); } else { document.title = 'done'; } };
            audio.play();
            """);
        // (running a script lays out anew, the title doesn't)
        (await engine.PrepareFrameAsync())!.Rasterize();
        var start = engine.Diagnostics.Frames.Count;
        var watch = Stopwatch.StartNew();

        while (engine.Title != "done" && watch.ElapsedMilliseconds < 5000)
        {
            (await engine.PrepareFrameAsync())?.Rasterize();
            await Task.Delay(20);
        }

        var kinds = engine.Diagnostics.Frames.Skip(start).Select(frame => frame.Kind).ToList();
        Assert.That(engine.Title, Is.EqualTo("done"));
        Assert.That(kinds, Is.Not.Empty);
        Assert.That(kinds, Has.None.EqualTo(FrameKind.Layout), string.Join(", ", kinds));
    }

    // controls at the top left (300x54), of a media of 30 seconds whose events are recorded
    private static async Task LoadControlsAsync(BrowserEngine engine)
    {
        await engine.LoadHtmlAsync("<title>Audio</title><body style='margin:0'><audio controls src='app:long.wav' style='display:block'></audio>", Address);
        await engine.EvaluateScriptAsync("""
            var audio = document.querySelector('audio'), events = [];
            ['play', 'playing', 'pause', 'volumechange'].forEach(function (name) {
                audio.addEventListener(name, function () { events.push(name); });
            });
            """);
        await Eventually(async () => await engine.EvaluateScriptAsync("audio.duration") is 30.0);
        await engine.PrepareFrameAsync();
    }

    private static BrowserEngine CreateEngine(IAudioOutput? output = null, bool allowAutoplay = false)
    {
        var options = new BrowserOptions
        {
            ProfileDirectory = null,
            ViewportWidth = 800,
            ViewportHeight = 600,
            AudioOutput = output,
            IsAutoplayAllowed = allowAutoplay,
        };
        options.SchemeHandlers["app"] = (request, _) =>
        {
            // the name is all after the scheme (app:tone.wav)
            var name = request.Address.Href["app:".Length..];
            var (type, content) = name switch
            {
                "long.wav" => ("audio/wav", CreateWav(8000, 1, 30, 16, false, 0.1)),
                "tone.m4a" => ("audio/mp4", new byte[64]),
                _ => (Path.GetExtension(name) switch
                {
                    ".wav" => "audio/wav",
                    ".mp3" => "audio/mpeg",
                    _ => "audio/ogg",
                }, File.ReadAllBytes(GetMediaPath(name))),
            };
            var response = new DefaultResponse
            {
                Address = request.Address,
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = new MemoryStream(content),
            };
            response.Headers[HeaderNames.ContentType] = type;
            return Task.FromResult<IResponse?>(response);
        };
        return new BrowserEngine(options);
    }

    private static string GetMediaPath(string name) => Path.Combine(TestContext.CurrentContext.TestDirectory, "Media", name);

    private static float[] ReadAll(IAudioDecoder decoder)
    {
        var samples = new List<float>();
        var buffer = new float[1024 * decoder.Channels];
        int count;

        while ((count = decoder.Read(buffer)) > 0)
        {
            samples.AddRange(buffer.Take(count));
        }

        return [.. samples];
    }

    private static byte[] CreateWav(int sampleRate, int channels, double seconds, int bits, bool isFloat, double value)
    {
        var frames = (int)(sampleRate * seconds);
        var blockAlign = channels * bits / 8;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + frames * blockAlign);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)(isFloat ? 3 : 1));
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * blockAlign);
        writer.Write((short)blockAlign);
        writer.Write((short)bits);
        writer.Write("data"u8);
        writer.Write(frames * blockAlign);

        for (var i = 0; i < frames * channels; i++)
        {
            switch (bits)
            {
                case 8:
                    writer.Write((byte)(128 + value * 128));
                    break;
                case 16:
                    writer.Write((short)(value * 32768));
                    break;
                case 24:
                    var sample = (int)(value * 8388608);
                    writer.Write((byte)sample);
                    writer.Write((byte)(sample >> 8));
                    writer.Write((byte)(sample >> 16));
                    break;
                default:
                    if (isFloat)
                    {
                        writer.Write((float)value);
                    }
                    else
                    {
                        writer.Write((int)(value * 2147483648));
                    }

                    break;
            }
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static async Task Eventually(Func<bool> condition) => await Eventually(() => Task.FromResult(condition()));

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        var watch = Stopwatch.StartNew();

        while (!await condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10))
            {
                Assert.Fail("Condition was not met in time.");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Plays silently (in real time), but records the formats and the loudest sample.
    /// </summary>
    private sealed class RecordingOutput : IAudioOutput
    {
        private readonly SilentAudioOutput _silent = new();
        private readonly ConcurrentQueue<AudioFormat> _formats = new();
        private int _openStreams;
        private float _peak;

        public IReadOnlyCollection<AudioFormat> Formats => [.. _formats];

        public int OpenStreams => Volatile.Read(ref _openStreams);

        public float Peak => Volatile.Read(ref _peak);

        public void Reset()
        {
            _formats.Clear();
            Volatile.Write(ref _peak, 0f);
        }

        public IAudioStream Open(AudioFormat format, IAudioSource source)
        {
            _formats.Enqueue(format);
            Interlocked.Increment(ref _openStreams);
            return new Stream(this, _silent.Open(format, new Source(this, source)));
        }

        private sealed class Source(RecordingOutput output, IAudioSource source) : IAudioSource
        {
            public void Read(Span<float> buffer)
            {
                source.Read(buffer);

                foreach (var sample in buffer)
                {
                    if (Math.Abs(sample) > output._peak)
                    {
                        Volatile.Write(ref output._peak, Math.Abs(sample));
                    }
                }
            }
        }

        private sealed class Stream(RecordingOutput output, IAudioStream stream) : IAudioStream
        {
            private int _isDisposed;

            public void Start() => stream.Start();

            public void Stop() => stream.Stop();

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
                {
                    stream.Dispose();
                    Interlocked.Decrement(ref output._openStreams);
                }
            }
        }
    }
}
