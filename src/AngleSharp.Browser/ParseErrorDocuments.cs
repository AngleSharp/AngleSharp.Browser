namespace AngleSharp.Browser;

using AngleSharp.Dom;
using AngleSharp.Io;
using AngleSharp.Xml.Parser;
using System;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Shows a navigated XML document that isn't well-formed like a browser does: as a document with
/// just the error (a <c>parsererror</c> element), which loads, instead of failing the navigation
/// (AngleSharp.Xml throws). Its scripts don't run, as the document was never parsed (Acid3).
/// </summary>
sealed class ParseErrorDocuments : IDocumentErrorHandler
{
    private const String ParserErrorNamespace = "http://www.mozilla.org/newlayout/xml/parsererror.xml";

    public async Task<IDocument?> HandleAsync(IBrowsingContext context, CreateDocumentOptions options, Exception error, CancellationToken cancellationToken)
    {
        if (error is not XmlParseException)
        {
            return null;
        }

        var content = $"<parsererror xmlns=\"{ParserErrorNamespace}\">{SecurityElement.Escape(error.Message)}</parsererror>";
        var response = VirtualResponse.Create(res => res
            .Address(options.Response.Address)
            .Status(options.Response.StatusCode)
            .Content(content)
            // the same kind of document (e.g. SVG), but the error is UTF-8
            .Header(HeaderNames.ContentType, options.ContentType.Content));
        var factory = context.GetFactory<IDocumentFactory>();
        return await factory.CreateAsync(context, new CreateDocumentOptions(response, null, options.ImportAncestor), cancellationToken).ConfigureAwait(false);
    }
}
