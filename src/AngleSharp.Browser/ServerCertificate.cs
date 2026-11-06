namespace AngleSharp.Browser;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// A certificate a server presented for a secure connection, as the platform verified it. It
/// is a snapshot, which stays valid after the connection is gone.
/// </summary>
public sealed class ServerCertificate
{
    private ServerCertificate(X509Certificate2 certificate, string host, SslPolicyErrors errors, IReadOnlyList<ServerCertificate> chain)
    {
        Host = host;
        Subject = certificate.Subject;
        Issuer = certificate.Issuer;
        CommonName = certificate.GetNameInfo(X509NameType.SimpleName, false);
        IssuerName = certificate.GetNameInfo(X509NameType.SimpleName, true);
        Organization = GetPart(certificate.SubjectName, "O");
        IssuerOrganization = GetPart(certificate.IssuerName, "O");
        NotBefore = certificate.NotBefore.ToUniversalTime();
        NotAfter = certificate.NotAfter.ToUniversalTime();
        SerialNumber = certificate.SerialNumber;
        Thumbprint = certificate.Thumbprint;
        Sha256Fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        SignatureAlgorithm = certificate.SignatureAlgorithm.FriendlyName ?? certificate.SignatureAlgorithm.Value ?? string.Empty;
        PublicKey = DescribeKey(certificate);
        AlternativeNames = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>()
            .SelectMany(m => m.EnumerateDnsNames())
            .ToArray();
        PolicyErrors = errors;
        Chain = chain;
        RawData = certificate.RawData;
    }

    /// <summary>
    /// Gets the host the connection was made to.
    /// </summary>
    public string Host { get; }

    /// <summary>
    /// Gets the distinguished name of the certificate's subject.
    /// </summary>
    public string Subject { get; }

    /// <summary>
    /// Gets the distinguished name of the certificate's issuer.
    /// </summary>
    public string Issuer { get; }

    /// <summary>
    /// Gets the common name of the subject, e.g., the host name.
    /// </summary>
    public string CommonName { get; }

    /// <summary>
    /// Gets the common name of the issuer.
    /// </summary>
    public string IssuerName { get; }

    /// <summary>
    /// Gets the organization of the subject, if the certificate names one.
    /// </summary>
    public string? Organization { get; }

    /// <summary>
    /// Gets the organization of the issuer, if the certificate names one.
    /// </summary>
    public string? IssuerOrganization { get; }

    /// <summary>
    /// Gets the start of the validity period (UTC).
    /// </summary>
    public DateTime NotBefore { get; }

    /// <summary>
    /// Gets the end of the validity period (UTC).
    /// </summary>
    public DateTime NotAfter { get; }

    /// <summary>
    /// Gets the serial number (hexadecimal).
    /// </summary>
    public string SerialNumber { get; }

    /// <summary>
    /// Gets the SHA-1 thumbprint (hexadecimal).
    /// </summary>
    public string Thumbprint { get; }

    /// <summary>
    /// Gets the SHA-256 fingerprint (hexadecimal).
    /// </summary>
    public string Sha256Fingerprint { get; }

    /// <summary>
    /// Gets the name of the signature algorithm, e.g., sha256RSA.
    /// </summary>
    public string SignatureAlgorithm { get; }

    /// <summary>
    /// Gets a description of the public key, e.g., "RSA (2048 bits)".
    /// </summary>
    public string PublicKey { get; }

    /// <summary>
    /// Gets the DNS names the certificate is valid for.
    /// </summary>
    public IReadOnlyList<string> AlternativeNames { get; }

    /// <summary>
    /// Gets what the platform found wrong when verifying the certificate.
    /// </summary>
    public SslPolicyErrors PolicyErrors { get; }

    /// <summary>
    /// Gets if the platform trusted the certificate for the host.
    /// </summary>
    public bool IsValid => PolicyErrors == SslPolicyErrors.None;

    /// <summary>
    /// Gets the chain of trust, from the issuer of this certificate up to the root. Empty for
    /// the certificates of the chain itself.
    /// </summary>
    public IReadOnlyList<ServerCertificate> Chain { get; }

    /// <summary>
    /// Gets the certificate in its DER encoding.
    /// </summary>
    public byte[] RawData { get; }

    /// <summary>
    /// Takes a snapshot of a certificate a server presented, e.g., in the validation callback
    /// of an HTTP handler.
    /// </summary>
    /// <param name="host">The host the connection was made to.</param>
    /// <param name="certificate">The certificate of the server.</param>
    /// <param name="chain">The chain the platform built for it, if any.</param>
    /// <param name="errors">What the platform found wrong with it.</param>
    public static ServerCertificate Create(string host, X509Certificate2 certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        var issuers = chain?.ChainElements
            .Skip(1)
            .Select(m => new ServerCertificate(m.Certificate, host, SslPolicyErrors.None, []))
            .ToArray() ?? [];
        return new ServerCertificate(certificate, host, errors, issuers);
    }

    private static string? GetPart(X500DistinguishedName name, string key) =>
        name.EnumerateRelativeDistinguishedNames()
            .Where(m => m.GetSingleElementType().FriendlyName == key || m.GetSingleElementType().Value == key)
            .Select(m => m.GetSingleElementValue())
            .FirstOrDefault(m => !string.IsNullOrEmpty(m));

    private static string DescribeKey(X509Certificate2 certificate)
    {
        using var rsa = certificate.GetRSAPublicKey();

        if (rsa is not null)
        {
            return $"RSA ({rsa.KeySize} bits)";
        }

        using var ecdsa = certificate.GetECDsaPublicKey();

        if (ecdsa is not null)
        {
            return $"ECDSA ({ecdsa.KeySize} bits)";
        }

        return certificate.PublicKey.Oid.FriendlyName ?? certificate.PublicKey.Oid.Value ?? "Unknown";
    }
}
