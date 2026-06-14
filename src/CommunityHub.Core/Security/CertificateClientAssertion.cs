using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace CommunityHub.Core.Security;

/// <summary>
/// Loads an X.509 certificate (with private key) from the machine / user
/// certificate store by thumbprint. Used for certificate-based SPN auth so no
/// client secret is ever placed on the wire — the ELDK SPN model (certificate
/// only; secrets deleted).
/// </summary>
public static class CertificateLoader
{
    /// <summary>
    /// Find a certificate with a usable private key by thumbprint, searching
    /// <c>LocalMachine\My</c> first (where the ELDK SPN certs live) then
    /// <c>CurrentUser\My</c>. Throws when not found or no private key.
    /// </summary>
    public static X509Certificate2 LoadByThumbprint(string thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            throw new InvalidOperationException("Certificate thumbprint is empty.");
        }

        var clean = thumbprint.Replace(" ", string.Empty).Replace("​", string.Empty).Trim().ToUpperInvariant();

        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            using var store = new X509Store(StoreName.My, location);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, clean, validOnly: false);
            if (found.Count > 0)
            {
                var cert = found[0];
                if (!cert.HasPrivateKey)
                {
                    throw new InvalidOperationException(
                        $"Certificate {clean} found in {location}\\My but has no accessible private key.");
                }
                return cert;
            }
        }

        throw new InvalidOperationException(
            $"Certificate with thumbprint {clean} not found in LocalMachine\\My or CurrentUser\\My.");
    }
}

/// <summary>
/// Builds an RFC 7523 client-assertion JWT (RS256) signed by an SPN certificate,
/// for the OAuth2 client-credentials flow (<c>client_assertion_type=
/// urn:ietf:params:oauth:client-assertion-type:jwt-bearer</c>). Microsoft identity
/// platform requires the <c>x5t</c> header (base64url SHA-1 cert thumbprint) so it
/// can match the assertion to an uploaded certificate.
/// </summary>
public static class ClientAssertionJwt
{
    public static string Build(string clientId, string tokenEndpoint, X509Certificate2 cert)
    {
        var now = DateTimeOffset.UtcNow;
        var thumbprint = cert.GetCertHash(); // SHA-1 hash bytes
        var x5t = Base64Url(thumbprint);

        var header = new Dictionary<string, object?>
        {
            ["alg"] = "RS256",
            ["typ"] = "JWT",
            ["x5t"] = x5t,
        };

        var payload = new Dictionary<string, object?>
        {
            ["aud"] = tokenEndpoint,
            ["iss"] = clientId,
            ["sub"] = clientId,
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["nbf"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(10).ToUnixTimeSeconds(),
        };

        var headerSeg  = Base64Url(JsonSerializer.SerializeToUtf8Bytes(header));
        var payloadSeg = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signingInput = $"{headerSeg}.{payloadSeg}";

        using var rsa = cert.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("Certificate has no RSA private key for JWT signing.");
        var signature = rsa.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return $"{signingInput}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
