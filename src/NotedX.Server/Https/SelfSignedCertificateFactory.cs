using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using NotedX.Core.Storage;

namespace NotedX.Server.Https;

/// <summary>Creates and caches the optional self-signed certificate used for LAN HTTPS.</summary>
public static class SelfSignedCertificateFactory
{
    private const string CertificateFileName = "notedx-cert.pem";
    private const string KeyFileName = "notedx-key.pem";

    /// <summary>
    /// Loads the persisted certificate, creating a new one when it is missing,
    /// unreadable, or about to expire.
    /// </summary>
    /// <param name="paths">Application paths; the certificate lives under <see cref="AppPaths.CertificatesDirectory"/>.</param>
    /// <param name="additionalHostNames">Extra DNS names to include besides localhost and the machine name.</param>
    /// <param name="logger">Optional logger; certificate contents are never logged.</param>
    public static X509Certificate2 LoadOrCreate(
        AppPaths paths,
        IEnumerable<string>? additionalHostNames = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        Directory.CreateDirectory(paths.CertificatesDirectory);
        var certificatePath = Path.Combine(paths.CertificatesDirectory, CertificateFileName);
        var keyPath = Path.Combine(paths.CertificatesDirectory, KeyFileName);

        if (File.Exists(certificatePath) && File.Exists(keyPath))
        {
            try
            {
                var existing = Load(certificatePath, keyPath);
                if (existing.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(30))
                {
                    return existing;
                }

                existing.Dispose();
                logger?.LogInformation("The self-signed HTTPS certificate is about to expire and will be renewed.");
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "The stored self-signed certificate could not be loaded; a new one will be created.");
            }
        }

        return Create(certificatePath, keyPath, additionalHostNames, logger);
    }

    private static X509Certificate2 Create(
        string certificatePath,
        string keyPath,
        IEnumerable<string>? additionalHostNames,
        ILogger? logger)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=noted-x",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, 0, critical: true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.1") },
                critical: false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddDnsName(Environment.MachineName);
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);

        if (additionalHostNames is not null)
        {
            foreach (var hostName in additionalHostNames.Where(name => !string.IsNullOrWhiteSpace(name)))
            {
                sanBuilder.AddDnsName(hostName);
            }
        }

        foreach (var address in Net.LanAddressProvider.GetAddresses())
        {
            if (IPAddress.TryParse(address.Address, out var ip))
            {
                sanBuilder.AddIpAddress(ip);
            }
        }

        request.CertificateExtensions.Add(sanBuilder.Build());

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddYears(5);
        using var certificate = request.CreateSelfSigned(notBefore, notAfter);

        File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
        File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());

        logger?.LogInformation(
            "Created a self-signed HTTPS certificate valid until {NotAfter:u}. Browsers will warn until it is trusted.",
            notAfter.UtcDateTime);

        return Load(certificatePath, keyPath);
    }

    private static X509Certificate2 Load(string certificatePath, string keyPath)
    {
        using var ephemeral = X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
        var pkcs12 = ephemeral.Export(X509ContentType.Pkcs12);
        return new X509Certificate2(pkcs12);
    }
}
