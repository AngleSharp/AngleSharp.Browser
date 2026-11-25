namespace AngleSharp.Browser;

using AngleSharp.Attributes;
using AngleSharp.Dom;
using AngleSharp.Dom.Events;
using AngleSharp.Html.Dom;
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

/// <summary>
/// The fullscreen state of the documents of an engine (Fullscreen API): an element shown
/// fullscreen covers the viewport (it matches <c>:fullscreen</c>), and so do the iframes of
/// the documents it is in. The engine shows the page fullscreen meanwhile.
/// </summary>
sealed class Fullscreen
{
    private const String PseudoClass = "fullscreen";

    /// <summary>
    /// Completes the rules of the user agent for the fullscreen elements (Fullscreen 6, in
    /// AngleSharp.Css): without a top layer, the element is above everything else, and without
    /// a ::backdrop, it is black behind the element.
    /// </summary>
    public const String DefaultStyle = ":not(:root):fullscreen { z-index: 2147483647 !important; background-color: black }";

    private static readonly ConditionalWeakTable<IDocument, IElement> Elements = new();

    private readonly Func<Boolean> _hasUserActivation;
    private readonly Action<Boolean> _changed;

    public Fullscreen(Func<Boolean> hasUserActivation, Action<Boolean> changed)
    {
        _hasUserActivation = hasUserActivation;
        _changed = changed;
    }

    /// <summary>
    /// Gets the fullscreen element of the document, if any.
    /// </summary>
    public static IElement? GetElement(IDocument document) =>
        Elements.TryGetValue(document, out var element) ? element : null;

    /// <summary>
    /// Gets if the documents of the context may show an element fullscreen: not in a sandbox
    /// without it, and in an iframe only with allowfullscreen (like its ancestors).
    /// </summary>
    public static Boolean IsEnabled(IDocument document)
    {
        for (var current = document; current is not null; current = current.Context?.Creator)
        {
            var context = current.Context;

            if (context is null || context.Security.HasFlag(Sandboxes.Fullscreen))
            {
                return false;
            }

            if (context.Parent is not null && FindFrame(current)?.HasAttribute(AttributeNames.AllowFullscreen) != true)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Shows the element fullscreen, if the page may (a user activation). Requires exclusive access.
    /// </summary>
    public Task Request(IElement element)
    {
        var document = element.Owner;

        if (document is null || !element.IsConnected() || !IsEnabled(document) || (GetElement(document) is null && !_hasUserActivation()))
        {
            if (document is not null)
            {
                Queue(document, () => element.Dispatch(new Event(EventNames.FullscreenError, bubbles: true, cancelable: false, composed: true)));
            }

            return Task.FromException(new InvalidOperationException("The element cannot be shown fullscreen."));
        }

        // the element and the frames of its document in the ancestors' documents are fullscreen
        for (IElement? current = element; current?.Owner is { } owner; current = FindFrame(owner))
        {
            if (GetElement(owner) is var previous && previous != current)
            {
                previous?.RemovePseudoClass(PseudoClass);
                current.SetPseudoClass(PseudoClass);
                Elements.AddOrUpdate(owner, current);
                var changed = current;
                Queue(owner, () => FireChange(changed, owner));
            }
        }

        _changed(true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Ends showing an element of the document fullscreen. Requires exclusive access.
    /// </summary>
    public Task Exit(IDocument document)
    {
        if (GetElement(document) is null)
        {
            return Task.FromException(new InvalidOperationException("The document has no fullscreen element."));
        }

        ExitAll(document);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Ends showing anything fullscreen in the document's top-level browsing context, e.g. for
    /// Esc. Requires exclusive access.
    /// </summary>
    public Boolean ExitAll(IDocument? document)
    {
        var top = document;

        while (top?.Context?.Creator is { } creator)
        {
            top = creator;
        }

        if (top is null || GetElement(top) is null)
        {
            return false;
        }

        for (var current = top; current is not null && GetElement(current) is { } element;)
        {
            Elements.Remove(current);
            element.RemovePseudoClass(PseudoClass);
            var owner = current;
            Queue(owner, () => FireChange(element, owner));
            current = (element as IHtmlInlineFrameElement)?.ContentDocument;
        }

        _changed(false);
        return true;
    }

    private static void FireChange(IElement element, IDocument document)
    {
        // an element removed meanwhile does not get it, the document does
        IEventTarget target = element.IsConnected() && element.Owner == document ? element : document;
        var ev = new Event(EventNames.FullscreenChange, bubbles: true, cancelable: false, composed: true);
        target.Dispatch(ev);
    }

    private static void Queue(IDocument document, Action action)
    {
        if (document.Context?.GetService<IEventLoop>() is { } loop)
        {
            loop.Enqueue(action);
        }
        else
        {
            action();
        }
    }

    private static IElement? FindFrame(IDocument document) =>
        document.Context?.Creator?.QuerySelectorAll("iframe").OfType<IHtmlInlineFrameElement>().FirstOrDefault(frame => frame.ContentDocument == document);
}

/// <summary>
/// The members of the Fullscreen API on elements.
/// </summary>
[DomExposed("Element")]
public static class FullscreenElementExtensions
{
    /// <summary>
    /// Shows the element fullscreen, which needs a user activation (a press or a key).
    /// </summary>
    /// <param name="element">The element to show.</param>
    /// <returns>The task finishing once it is shown.</returns>
    [DomName("requestFullscreen")]
    public static Task RequestFullscreen(this IElement element) =>
        element.Owner?.Context?.GetService<Fullscreen>() is { } fullscreen
            ? fullscreen.Request(element)
            : Task.FromException(new NotSupportedException("Fullscreen is not supported."));
}

/// <summary>
/// The members of the Fullscreen API on documents.
/// </summary>
[DomExposed("Document")]
public static class FullscreenDocumentExtensions
{
    /// <summary>
    /// Gets the element shown fullscreen, if any.
    /// </summary>
    [DomName("fullscreenElement")]
    [DomAccessor(Accessors.Getter)]
    public static IElement? GetFullscreenElement(this IDocument document) => Fullscreen.GetElement(document);

    /// <summary>
    /// Gets if an element is shown fullscreen.
    /// </summary>
    [DomName("fullscreen")]
    [DomAccessor(Accessors.Getter)]
    public static Boolean IsFullscreen(this IDocument document) => Fullscreen.GetElement(document) is not null;

    /// <summary>
    /// Gets if elements of the document may be shown fullscreen.
    /// </summary>
    [DomName("fullscreenEnabled")]
    [DomAccessor(Accessors.Getter)]
    public static Boolean IsFullscreenEnabled(this IDocument document) =>
        document.Context?.GetService<Fullscreen>() is not null && Fullscreen.IsEnabled(document);

    /// <summary>
    /// Ends showing an element fullscreen.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <returns>The task finishing once it ended.</returns>
    [DomName("exitFullscreen")]
    public static Task ExitFullscreen(this IDocument document) =>
        document.Context?.GetService<Fullscreen>() is { } fullscreen
            ? fullscreen.Exit(document)
            : Task.FromException(new InvalidOperationException("The document has no fullscreen element."));
}
