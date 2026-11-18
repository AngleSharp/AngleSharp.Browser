namespace AngleSharp.Browser;

using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Dom.Events;
using AngleSharp.Renderer;

/// <summary>
/// Selecting text with the pointer (see <see cref="BrowserOptions.IsTextSelectionEnabled"/>): the
/// harness of the document pressed in selects, and shows the selection without a layout.
/// </summary>
partial class BrowserEngine
{
    // the harness selecting text while the button is held, and the document it shows
    private (IDomHarness Harness, IDocument Document)? _selecting;

    /// <summary>
    /// Fires <c>copy</c> at the focused element and gets the selected text as it is shown, e.g.,
    /// for Ctrl+C. Gets an empty text if nothing is selected or the page canceled copying.
    /// </summary>
    /// <param name="cancellationToken">The token to cancel waiting for the engine.</param>
    public Task<string> CopySelectionAsync(CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(() =>
        {
            if (!_options.IsTextSelectionEnabled || FocusedDocument is not { Context: { } context } document)
            {
                return string.Empty;
            }

            var text = GetHarness(document, context).SelectedText;

            if (text.Length == 0 || GetKeyboardTarget() is not IElement target ||
                !Fire(target, new Event("copy", bubbles: true, cancelable: true)))
            {
                return string.Empty;
            }

            return text;
        }, cancellationToken);

    /// <summary>
    /// Starts selecting at the position, after a press whose <c>mousedown</c> wasn't canceled, in
    /// the document of what was hit there. Requires exclusive access.
    /// </summary>
    private void BeginTextSelection(IElement? hit, double x, double y, int clickCount, InputModifiers modifiers)
    {
        _selecting = null;

        if (!_options.IsTextSelectionEnabled || (hit?.Owner ?? _document) is not { Context: { } context } document)
        {
            return;
        }

        var harness = GetHarness(document, context);
        var (clientX, clientY) = ToSelectionClient(hit, x, y);

        if (harness.BeginTextSelection(clientX, clientY, clickCount, modifiers.HasFlag(InputModifiers.Shift)))
        {
            _selecting = (harness, document);
        }
    }

    /// <summary>
    /// Extends the selection to the position, while the button is held. Requires exclusive access.
    /// </summary>
    private void ExtendTextSelection(double x, double y)
    {
        if (_selecting is not { } selecting || !IsInPage(selecting.Document))
        {
            _selecting = null;
            return;
        }

        var (clientX, clientY) = ToSelectionClient(selecting.Document.DocumentElement, x, y);
        selecting.Harness.ExtendTextSelection(clientX, clientY);
    }

    /// <summary>
    /// Ends selecting, as the button was released. Requires exclusive access.
    /// </summary>
    private void EndTextSelection()
    {
        _selecting?.Harness.EndTextSelection();
        _selecting = null;
    }

    /// <summary>
    /// Selects all text of the focused document, e.g., for Ctrl+A. Requires exclusive access.
    /// </summary>
    private void SelectAllText()
    {
        if (_options.IsTextSelectionEnabled && FocusedDocument is { Context: { } context } document)
        {
            GetHarness(document, context).SelectAllText();
        }
    }

    private IDomHarness GetHarness(IDocument document, IBrowsingContext context)
    {
        if (ReferenceEquals(document, _document))
        {
            return _harness;
        }

        // the harness of an iframe's document is the renderer's, which selects by default
        var harness = context.GetDomHarness();
        harness.IsTextSelectionEnabled = _options.IsTextSelectionEnabled;
        return harness;
    }

    /// <summary>
    /// The position in the viewport of the element's document as its harness sees it: the page
    /// may be shown scrolled ahead of the DOM.
    /// </summary>
    private (double X, double Y) ToSelectionClient(IElement? element, double x, double y) =>
        ToClient(element, x, y + ScrollPosition - ScrollTop);
}
