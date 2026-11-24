namespace AngleSharp.Browser;

using AngleSharp.Dom;
using AngleSharp.Io;
using AngleSharp.Text;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// The document factory of the engine: creates documents like AngleSharp does, but shows
/// responses that aren't documents like a browser does (e.g. a navigated image).
/// </summary>
sealed class BrowserDocumentFactory : DefaultDocumentFactory
{
    public override Task<IDocument> CreateAsync(IBrowsingContext context, CreateDocumentOptions options, CancellationToken cancellationToken)
    {
        var contentType = options.ContentType;

        // an image (other than SVG, which has a creator of its own) is shown in a document of
        // its own, its data isn't markup (e.g. a PNG in an iframe)
        if (contentType.GeneralType.Is("image") && !contentType.Content.Is(MimeTypeNames.Svg))
        {
            return LoadImageAsync(context, options, cancellationToken);
        }

        return base.CreateAsync(context, options, cancellationToken);
    }

    /// <summary>
    /// Shows the response, an image, in a document with just that image. The document keeps the
    /// address, headers and content type (e.g. <c>image/png</c>) of the response, but its content
    /// is the markup showing the image, which completes (with a load event) once the image is there.
    /// </summary>
    private static async Task<IDocument> LoadImageAsync(IBrowsingContext context, CreateDocumentOptions options, CancellationToken cancellationToken)
    {
        var original = options.Response;
        var address = original.Address.Href;
        var content = $"<html><head></head><body><img src=\"{WebUtility.HtmlEncode(address)}\"></body></html>";
        var response = VirtualResponse.Create(res =>
        {
            res.Address(original.Address).Status(original.StatusCode).Content(content);

            foreach (var header in original.Headers)
            {
                res.Header(header.Key, header.Value);
            }
        });
        var imageOptions = new CreateDocumentOptions(response, null, options.ImportAncestor)
        {
            IsScriptingDisabled = options.IsScriptingDisabled,
            IsNavigationDisabled = options.IsNavigationDisabled,
        };
        var document = await LoadHtmlAsync(context, imageOptions, cancellationToken).ConfigureAwait(false);
        // the image itself is loaded by the img element, the data of the navigation isn't needed
        original.Dispose();
        return document;
    }
}
