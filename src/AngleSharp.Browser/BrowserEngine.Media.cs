namespace AngleSharp.Browser;

using System.Linq;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

/// <summary>
/// The controls the renderer paints for media (`audio[controls]`): a press on the play button
/// plays or pauses, one on the mute button mutes, and the timeline seeks while it is dragged.
/// </summary>
partial class BrowserEngine
{
    // the media whose timeline is dragged while the button is held
    private IHtmlMediaElement? _seekingMedia;
    // if a media changed what its controls would show since the last frame
    private volatile bool _hasMediaChanged;

    /// <summary>
    /// Acts on a press on the controls of a media element, if it was one. Requires exclusive access.
    /// </summary>
    private bool PressMediaControls(IElement? target, double x, double y)
    {
        if (target is not IHtmlMediaElement media || !media.HasMediaControls())
        {
            return false;
        }

        var (left, top) = ToClient(media, x, y);

        switch (media.GetMediaControlAt(left, top))
        {
            case MediaControlPart.PlayPause when media.IsPaused || media.IsEnded:
                // a page may not play it (e.g. no source), which its promise tells the page alone
                media.PlayAsync().ContinueWith(task => task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                break;
            case MediaControlPart.PlayPause:
                media.Pause();
                break;
            case MediaControlPart.Mute:
                media.IsMuted = !media.IsMuted;
                break;
            case MediaControlPart.Timeline:
                _seekingMedia = media;
                SeekMedia(x);
                break;
        }

        // the controls show the state of the element, which changed without a mutation
        Invalidate();
        return true;
    }

    /// <summary>
    /// Seeks the media whose timeline is dragged to the position of the pointer. Requires exclusive access.
    /// </summary>
    private void SeekMedia(double x)
    {
        if (_seekingMedia is not { } media)
        {
            return;
        }

        var duration = media.Duration;

        if (media.Owner is null || !double.IsFinite(duration) || media.GetMediaTimelinePosition(ToClient(media, x, 0).X) is not { } position)
        {
            return;
        }

        media.CurrentTime = position * duration;
        Invalidate();
    }

    // the media changed what its controls show (it may be on another thread); only the
    // controls show it, so a page without them (e.g. a game playing its sounds) keeps its frame
    private void OnMediaChanged()
    {
        if (!_isDisposed)
        {
            _hasMediaChanged = true;
            Invalidate(isMutation: true);
        }
    }

    /// <summary>
    /// Lays out the next frame anew if a media changed since the last one and the page (or one
    /// of its iframes) shows the controls of a media. Requires exclusive access.
    /// </summary>
    private void SyncMediaChanges(IDocument document)
    {
        if (_hasMediaChanged)
        {
            _hasMediaChanged = false;

            if (ShowsMediaControls(document))
            {
                _needsFullLayout = true;
            }
        }
    }

    private static bool ShowsMediaControls(IDocument document) =>
        document.QuerySelector("audio[controls]") is not null ||
        document.QuerySelectorAll("iframe").OfType<IHtmlInlineFrameElement>().Any(frame => frame.ContentDocument is { } content && ShowsMediaControls(content));
}
