namespace Vision.Pages;

using System;
using AngleSharp.Browser;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Security;
using System.Text;
using AngleSharp.Dom;

/// <summary>
/// The site information: how the connection to the site of a page is secured, i.e., the
/// certificate its server presented and who vouches for it.
/// </summary>
public sealed class CertificatePage(string? colorScheme, string? url, ServerCertificate? certificate, Action close) : HtmlPage("certificate", colorScheme)
{
    protected override void Wire(IDocument document)
    {
        var uri = Uri.TryCreate(url, UriKind.Absolute, out var value) ? value : null;
        document.GetElementById("host")!.TextContent = uri is { Host.Length: > 0 } ? uri.Host : url ?? "No page";
        document.GetElementById("address")!.TextContent = url ?? string.Empty;
        document.GetElementById("content")!.InnerHtml = Describe(uri);
        OnClick(document, "close", () => OnUiThread(close));
    }

    private string Describe(Uri? uri)
    {
        if (uri?.Scheme != Uri.UriSchemeHttps)
        {
            return Verdict("insecure", "Connection is not secure",
                uri?.Scheme == Uri.UriSchemeHttp
                    ? "The page was loaded over HTTP: what you send to or receive from this site could be read or changed by others on the network."
                    : "This page was not loaded from a web server, so there is no connection to secure.");
        }

        if (certificate is null)
        {
            return Verdict("unknown", "Certificate not available",
                "The page was loaded over HTTPS, but without a new connection to its server (e.g., from the cache). Reloading the page shows the certificate.");
        }

        var html = new StringBuilder();
        html.Append(certificate.IsValid
            ? Verdict("secure", "Connection is secure", $"The certificate of {certificate.Host} was verified and issued by a trusted authority.")
            : Verdict("insecure", "Certificate is not trusted", DescribeErrors(certificate.PolicyErrors)));

        html.Append(Section("Issued to",
            Pair("Common name", certificate.CommonName),
            Pair("Organization", certificate.Organization ?? "(not part of the certificate)"),
            RawPair("Valid for", certificate.AlternativeNames.Count > 0
                ? string.Concat(certificate.AlternativeNames.Select(m => $"""<span class="chip">{Encode(m)}</span>"""))
                : Encode(certificate.CommonName))));
        html.Append(Section("Issued by",
            Pair("Common name", certificate.IssuerName),
            Pair("Organization", certificate.IssuerOrganization ?? "(not part of the certificate)")));
        html.Append(Section("Validity",
            Pair("Issued on", FormatDate(certificate.NotBefore)),
            RawPair("Expires on", DescribeExpiry(certificate.NotAfter))));
        html.Append(Section("Fingerprints",
            MonoPair("SHA-256", FormatHex(certificate.Sha256Fingerprint)),
            MonoPair("SHA-1", FormatHex(certificate.Thumbprint))));
        html.Append(Section("Details",
            MonoPair("Serial number", FormatHex(certificate.SerialNumber)),
            Pair("Signature algorithm", certificate.SignatureAlgorithm),
            Pair("Public key", certificate.PublicKey),
            Pair("Subject", certificate.Subject),
            Pair("Issuer", certificate.Issuer)));
        html.Append(Section("Certification path", CertificationPath(certificate)));
        return html.ToString();
    }

    /// <summary>
    /// The chain from the root down to the site, like the tree of other browsers.
    /// </summary>
    private static string CertificationPath(ServerCertificate certificate)
    {
        var path = certificate.Chain.Reverse().Append(certificate).ToList();
        var html = new StringBuilder("""<div class="path">""");

        for (var i = 0; i < path.Count; i++)
        {
            var item = path[i];
            var isLeaf = i == path.Count - 1;
            var role = isLeaf ? "Site" : i == 0 && path.Count > 1 ? "Root authority" : "Intermediate authority";
            html.Append($"""
                <div class="step{(isLeaf ? " leaf" : string.Empty)}" style="padding-left:{i * 18}px">{(i > 0 ? "└ " : string.Empty)}<span class="who">{Encode(item.CommonName)}</span> <span class="muted">· {role}, until {Encode(item.NotAfter.ToLocalTime().ToString("d", CultureInfo.CurrentCulture))}</span></div>
                """);
        }

        return html.Append("</div>").ToString();
    }

    private static string DescribeErrors(SslPolicyErrors errors)
    {
        var reasons = new List<string>();

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            reasons.Add("it was issued for a different site");
        }

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
        {
            reasons.Add("it is not issued by a trusted authority, or has expired or been revoked");
        }

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            reasons.Add("the server presented none");
        }

        return $"The connection was refused, as the certificate cannot be trusted: {string.Join("; ", reasons)}.";
    }

    private static string DescribeExpiry(DateTime notAfter)
    {
        var days = (int)Math.Floor((notAfter - DateTime.UtcNow).TotalDays);
        var date = Encode(FormatDate(notAfter));

        return days switch
        {
            < 0 => $"""{date} <span class="expired">(expired)</span>""",
            0 => $"""{date} <span class="muted">(today)</span>""",
            1 => $"""{date} <span class="muted">(in 1 day)</span>""",
            _ => $"""{date} <span class="muted">(in {days} days)</span>""",
        };
    }

    private static string FormatDate(DateTime value) =>
        value.ToLocalTime().ToString("dddd, MMMM d, yyyy 'at' HH:mm", CultureInfo.CurrentCulture);

    /// <summary>
    /// Writes bytes in pairs, which can wrap.
    /// </summary>
    private static string FormatHex(string hex) =>
        string.Join(' ', Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));

    private static string Verdict(string kind, string title, string text) =>
        $"""<div class="verdict {kind}"><strong>{Encode(title)}</strong>{Encode(text)}</div>""";

    private static string Section(string title, params string[] rows) =>
        $"""<div class="section"><h2>{Encode(title)}</h2>{string.Concat(rows)}</div>""";

    private static string Pair(string name, string value) => RawPair(name, Encode(value));

    private static string MonoPair(string name, string value) =>
        $"""<div class="pair"><div class="name">{Encode(name)}</div><div class="value mono">{Encode(value)}</div></div>""";

    private static string RawPair(string name, string html) =>
        $"""<div class="pair"><div class="name">{Encode(name)}</div><div class="value">{html}</div></div>""";
}
