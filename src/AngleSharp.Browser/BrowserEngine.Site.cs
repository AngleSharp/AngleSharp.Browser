namespace AngleSharp.Browser;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Io;

/// <summary>
/// What a browser shows about the site itself: its icon and the certificate of its connection.
/// </summary>
public sealed partial class BrowserEngine
{
    private const long MaxIconSize = 1024 * 1024;

    private readonly ConcurrentDictionary<string, ServerCertificate> _certificates = new(StringComparer.OrdinalIgnoreCase);
    private volatile IReadOnlyList<string> _iconUrls = [];

    /// <summary>
    /// Gets the address of the current page's icon, i.e., the first of <see cref="IconUrls"/>.
    /// </summary>
    public string? IconUrl => _iconUrls.Count > 0 ? _iconUrls[0] : null;

    /// <summary>
    /// Gets the addresses the current page's icon may come from, best first: the bitmaps
    /// declared by links (rel="icon", then rel="apple-touch-icon"), /favicon.ico of an HTTP(S)
    /// page, and declared SVG icons, which not every host can show. Changes raise
    /// <see cref="StateChanged"/>.
    /// </summary>
    public IReadOnlyList<string> IconUrls => _iconUrls;

    /// <summary>
    /// Gets the certificate the server of the current page presented, if the page came over
    /// HTTPS and the engine connected to its server (a page from the HTTP cache may not need
    /// to).
    /// </summary>
    public ServerCertificate? Certificate =>
        Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? GetCertificate(uri.Host)
            : null;

    /// <summary>
    /// Gets the certificate the given host presented the last time the engine connected to it.
    /// </summary>
    public ServerCertificate? GetCertificate(string host) => _certificates.TryGetValue(host, out var certificate) ? certificate : null;

    /// <summary>
    /// Loads the page's icon with the page's cookies and cache: the first of
    /// <see cref="IconUrls"/> that turns out to be an image (whatever its server claims it to
    /// be). Returns its content, or null if there is none.
    /// </summary>
    public async Task<byte[]?> LoadIconAsync(CancellationToken cancellationToken = default)
    {
        foreach (var address in _iconUrls)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await LoadIconAsync(address, cancellationToken).ConfigureAwait(false) is { } content && IsImage(content))
                {
                    return content;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // a broken candidate leaves the others
            }
        }

        return null;
    }

    private async Task<byte[]?> LoadIconAsync(string address, CancellationToken cancellationToken)
    {
        if (address.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return DecodeDataUrl(address);
        }

        if (_context.GetService<IDocumentLoader>() is not { } loader)
        {
            return null;
        }

        var download = loader.FetchAsync(new DocumentRequest(new Url(address)));

        using (cancellationToken.Register(download.Cancel))
        {
            using var response = await download.Task.ConfigureAwait(false);

            if ((int)response.StatusCode is < 200 or >= 300 || response.Content is null)
            {
                return null;
            }

            using var content = new MemoryStream();
            var buffer = new byte[81920];
            int read;

            while ((read = await response.Content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (content.Length + read > MaxIconSize)
                {
                    return null;
                }

                content.Write(buffer, 0, read);
            }

            return content.ToArray();
        }
    }

    /// <summary>
    /// Remembers the certificates servers present, while keeping the platform's verdict.
    /// </summary>
    internal static void TrackCertificates(HttpClientHandler handler, ConcurrentDictionary<string, ServerCertificate> certificates)
    {
        handler.ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) =>
        {
            if (certificate is not null && request.RequestUri is { } uri)
            {
                certificates[uri.Host] = ServerCertificate.Create(uri.Host, certificate, chain, errors);
            }

            return errors == SslPolicyErrors.None;
        };
    }

    /// <summary>
    /// Finds the icons of the active document. Requires exclusive access.
    /// </summary>
    private void SyncIcon(IDocument? document)
    {
        var icons = document is null ? [] : FindIcons(document);

        if (!icons.SequenceEqual(_iconUrls))
        {
            _iconUrls = icons;
            OnStateChanged();
        }
    }

    private static string[] FindIcons(IDocument document)
    {
        var links = document.Head?.Children
            .OfType<IHtmlLinkElement>()
            .Where(m => !string.IsNullOrWhiteSpace(m.Href))
            .Select(m => (Link: m, Kind: GetIconKind(m)))
            .Where(m => m.Kind > 0)
            .ToList() ?? [];
        var bitmaps = links.Where(m => !IsSvg(m.Link)).OrderBy(m => m.Kind).Select(m => m.Link.Href!);
        var site = Uri.TryCreate(document.Url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? [new Uri(uri, "/favicon.ico").ToString()]
            : Array.Empty<string>();
        var vectors = links.Where(m => IsSvg(m.Link)).Select(m => m.Link.Href!);
        return bitmaps.Concat(site).Concat(vectors).Distinct().ToArray();
    }

    /// <summary>
    /// Gets 1 for an icon, 2 for an icon of Apple's home screen, else 0.
    /// </summary>
    private static int GetIconKind(IHtmlLinkElement link)
    {
        var relations = link.Relation?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [];

        if (relations.Any(r => r.Equals("icon", StringComparison.OrdinalIgnoreCase)))
        {
            return 1;
        }

        return relations.Any(r => r.StartsWith("apple-touch-icon", StringComparison.OrdinalIgnoreCase)) ? 2 : 0;
    }

    private static bool IsSvg(IHtmlLinkElement link) =>
        link.Type?.Contains("svg", StringComparison.OrdinalIgnoreCase) == true ||
        link.Href!.Split('?', '#')[0].EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
        link.Href.StartsWith("data:image/svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Tells images from what else servers send (e.g., an error page) by their content, as
    /// icons are often served with any content type.
    /// </summary>
    private static bool IsImage(byte[] content)
    {
        ReadOnlySpan<byte> data = content;

        return data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47]) ||
            data.StartsWith((ReadOnlySpan<byte>)[0, 0, 1, 0]) || data.StartsWith((ReadOnlySpan<byte>)[0, 0, 2, 0]) ||
            data.StartsWith("GIF8"u8) ||
            data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]) ||
            data.StartsWith("BM"u8) ||
            data.Length > 12 && data.StartsWith("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8) ||
            System.Text.Encoding.UTF8.GetString(data[..Math.Min(data.Length, 1024)]).Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[]? DecodeDataUrl(string address)
    {
        var comma = address.IndexOf(',');

        if (comma < 0 || !address[..comma].Contains("image/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var data = Uri.UnescapeDataString(address[(comma + 1)..]);

        try
        {
            return address[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(data)
                : System.Text.Encoding.UTF8.GetBytes(data);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
