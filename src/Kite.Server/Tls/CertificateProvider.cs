using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Kite.Core.Models;

namespace Kite.Server.Tls;

/// <summary>
/// Manages X.509 certificates for SNI (RFC 6066) and ALPN (RFC 7301) bindings.
/// Supports loading PFX files from disk or generating ephemeral self-signed SAN certificates for development/testing.
/// </summary>
public sealed class CertificateProvider : IDisposable
{
    private readonly ConcurrentDictionary<string, X509Certificate2> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _provisioningModes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentBag<X509Certificate2> _retiredCerts = [];

    public X509Certificate2 GetOrCreateCertificate(
        SiteBindingConfig binding,
        string? configBaseDir = null,
        string? acmeCertsDir = null)
    {
        string hostKey = string.IsNullOrWhiteSpace(binding.Host) || binding.Host == "*"
            ? "localhost"
            : binding.Host.Trim();

        if (!string.IsNullOrWhiteSpace(binding.Tls?.CertificatePath))
        {
            string certPath = binding.Tls.CertificatePath;
            if (!Path.IsPathRooted(certPath) && !string.IsNullOrWhiteSpace(configBaseDir))
            {
                certPath = Path.Combine(configBaseDir, certPath);
            }

            if (File.Exists(certPath))
            {
                _provisioningModes[hostKey] = "ManualPfx";
                return _cache.GetOrAdd(
                    $"file:{certPath}",
                    _ => X509CertificateLoader.LoadPkcs12FromFile(certPath, binding.Tls.CertificatePassword));
            }
        }

        // Check ACME-provisioned certificate cache or config/certs/<domain>.pfx
        if (_cache.TryGetValue($"acme:{hostKey}", out X509Certificate2? acmeCached))
        {
            return acmeCached;
        }

        if (!string.IsNullOrWhiteSpace(acmeCertsDir))
        {
            string pfxPath = Path.Combine(acmeCertsDir, $"{Kite.Core.Storage.KiteHomeLayout.SanitizeFileName(hostKey)}.pfx");
            if (File.Exists(pfxPath))
            {
                X509Certificate2 loaded = _cache.GetOrAdd(
                    $"acme:{hostKey}",
                    _ => X509CertificateLoader.LoadPkcs12FromFile(pfxPath, "kite-acme"));
                _provisioningModes[hostKey] = "AcmeAuto";
                return loaded;
            }
        }

        return _cache.GetOrAdd(
            $"san:{hostKey}",
            _ =>
            {
                _provisioningModes[hostKey] = "DevelopmentSAN";
                if (string.Equals(hostKey, "localhost", StringComparison.OrdinalIgnoreCase) &&
                    TryGetLocalhostDevCertificate(out X509Certificate2? devCert) &&
                    devCert is not null)
                {
                    return devCert;
                }

                return GenerateSelfSignedCertificate(hostKey);
            });
    }

    public void InstallOrRotateCertificate(string host, X509Certificate2 certificate, string provisioningMode = "AcmeAuto")
    {
        string key = $"acme:{host.Trim()}";
        _cache.AddOrUpdate(
            key,
            certificate,
            (_, existing) =>
            {
                if (!ReferenceEquals(existing, certificate))
                {
                    _retiredCerts.Add(existing);
                }
                return certificate;
            });
        _provisioningModes[host.Trim()] = provisioningMode;
    }

    public bool NeedsAcmeProvisionOrRenewal(string host, int renewBeforeDays, DateTimeOffset nowUtc, string? acmeCertsDir = null)
    {
        string cleanHost = host.Trim();
        X509Certificate2? current = null;
        if (_cache.TryGetValue($"acme:{cleanHost}", out X509Certificate2? cached))
        {
            current = cached;
        }
        else if (!string.IsNullOrWhiteSpace(acmeCertsDir))
        {
            string pfxPath = Path.Combine(acmeCertsDir, $"{Kite.Core.Storage.KiteHomeLayout.SanitizeFileName(cleanHost)}.pfx");
            if (File.Exists(pfxPath))
            {
                try
                {
                    current = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, "kite-acme");
                    _cache[$"acme:{cleanHost}"] = current;
                    _provisioningModes[cleanHost] = "AcmeAuto";
                }
                catch
                {
                    current = null;
                }
            }
        }

        if (current is null)
        {
            return true;
        }

        DateTimeOffset notAfter = new DateTimeOffset(current.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        return notAfter - nowUtc <= TimeSpan.FromDays(Math.Max(1, renewBeforeDays));
    }

    private static bool TryGetLocalhostDevCertificate(out X509Certificate2? certificate)
    {
        certificate = null;
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            DateTime now = DateTime.UtcNow;

            X509Certificate2? best = store.Certificates
                .Find(X509FindType.FindBySubjectName, "localhost", validOnly: false)
                .Where(c => c.HasPrivateKey && c.NotBefore.ToUniversalTime() <= now && c.NotAfter.ToUniversalTime() > now)
                .OrderByDescending(c => c.NotAfter)
                .FirstOrDefault();

            if (best is not null)
            {
                certificate = best;
                return true;
            }
        }
        catch
        {
            // Fall back to generated self-signed certificate
        }

        return false;
    }

    public static X509Certificate2 GenerateSelfSignedCertificate(string dnsHostName)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            $"CN={dnsHostName}, O=Kite Web Server",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: false));

        var eku = new OidCollection { new("1.3.6.1.5.5.7.3.1") }; // Server Authentication
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, critical: false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(dnsHostName);
        if (!string.Equals(dnsHostName, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            sanBuilder.AddDnsName("localhost");
        }
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);
        req.CertificateExtensions.Add(sanBuilder.Build());

        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 ephemeral = req.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(1));

        // Export and reload via PKCS12 so Windows SChannel can access the private key
        byte[] pfxBytes = ephemeral.Export(X509ContentType.Pfx, "kite-ephemeral");
        try
        {
            return X509CertificateLoader.LoadPkcs12(
                pfxBytes,
                "kite-ephemeral",
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
        }
        catch
        {
            return X509CertificateLoader.LoadPkcs12(
                pfxBytes,
                "kite-ephemeral",
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
        }
    }

    public void Dispose()
    {
        foreach (var kvp in _cache)
        {
            kvp.Value.Dispose();
        }
        _cache.Clear();

        while (_retiredCerts.TryTake(out X509Certificate2? retired))
        {
            retired.Dispose();
        }
    }

    public TlsCertificateChainStatus? TryInspectCertificateStatus(SiteBindingConfig binding, bool allowGenerateSelfSigned, string? configBaseDir = null, bool mtlsEnabled = false)
    {
        string hostKey = string.IsNullOrWhiteSpace(binding.Host) || binding.Host == "*"
            ? "localhost"
            : binding.Host.Trim();

        X509Certificate2? cert = null;
        if (!string.IsNullOrWhiteSpace(binding.Tls?.CertificatePath))
        {
            string certPath = binding.Tls.CertificatePath;
            if (!Path.IsPathRooted(certPath) && !string.IsNullOrWhiteSpace(configBaseDir))
            {
                certPath = Path.Combine(configBaseDir, certPath);
            }

            if (_cache.TryGetValue($"file:{certPath}", out X509Certificate2? cachedFileCert))
            {
                cert = cachedFileCert;
            }
            else if (allowGenerateSelfSigned)
            {
                cert = GetOrCreateCertificate(binding, configBaseDir);
            }
        }
        else if (_cache.TryGetValue($"acme:{hostKey}", out X509Certificate2? cachedAcmeCert))
        {
            cert = cachedAcmeCert;
        }
        else if (_cache.TryGetValue($"san:{hostKey}", out X509Certificate2? cachedSanCert))
        {
            cert = cachedSanCert;
        }
        else if (allowGenerateSelfSigned)
        {
            cert = GetOrCreateCertificate(binding, configBaseDir);
        }

        string mode = _provisioningModes.GetValueOrDefault(hostKey, "DevelopmentSAN");
        if (cert is null)
        {
            return new TlsCertificateChainStatus
            {
                Host = hostKey,
                Port = binding.Port,
                Subject = $"CN={hostKey}",
                Issuer = "CN=Kite Development CA (On-Demand SAN)",
                Thumbprint = "ON-DEMAND",
                NotBeforeUtc = DateTimeOffset.UtcNow,
                NotAfterUtc = DateTimeOffset.UtcNow.AddDays(365),
                DaysRemaining = 365,
                Tls13Supported = binding.Tls?.AllowTls13 ?? true,
                ProvisioningMode = mode,
                MtlsEnabled = mtlsEnabled
            };
        }

        DateTimeOffset notBefore = new DateTimeOffset(cert.NotBefore.ToUniversalTime(), TimeSpan.Zero);
        DateTimeOffset notAfter = new DateTimeOffset(cert.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        int daysRemaining = Math.Max(0, (int)Math.Floor((notAfter - DateTimeOffset.UtcNow).TotalDays));

        return new TlsCertificateChainStatus
        {
            Host = hostKey,
            Port = binding.Port,
            Subject = cert.Subject,
            Issuer = cert.Issuer,
            Thumbprint = cert.Thumbprint,
            NotBeforeUtc = notBefore,
            NotAfterUtc = notAfter,
            DaysRemaining = daysRemaining,
            Tls13Supported = binding.Tls?.AllowTls13 ?? true,
            ProvisioningMode = mode,
            MtlsEnabled = mtlsEnabled
        };
    }

    public TlsCertificateChainStatus InspectCertificateStatus(SiteBindingConfig binding, string? configBaseDir = null) =>
        TryInspectCertificateStatus(binding, allowGenerateSelfSigned: true, configBaseDir)!;
}
