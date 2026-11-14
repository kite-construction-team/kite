using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Kite.Core.Models;
using Kite.Core.Serialization;
using Kite.Core.Storage;

namespace Kite.Server.Tls;

/// <summary>
/// Dependency-free RFC 8555 ACME v2 client supporting HTTP-01 and RFC 8737 TLS-ALPN-01
/// automated certificate issuance, renewal, and zero-downtime hot rotation.
/// </summary>
public sealed class AcmeCertificateManager : IDisposable
{
    private readonly KiteHomeLayout _layout;
    private readonly CertificateProvider _certificateProvider;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _renewLock = new(1, 1);

    private readonly ConcurrentDictionary<string, string> _http01Challenges = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, X509Certificate2> _tlsAlpnChallenges = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (bool Allowed, DateTimeOffset ExpiresUtc)> _onDemandHostCache = new(StringComparer.OrdinalIgnoreCase);

    public AcmeCertificateManager(
        KiteHomeLayout layout,
        CertificateProvider certificateProvider,
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(certificateProvider);
        _layout = layout;
        _certificateProvider = certificateProvider;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool TryResolveHttp01Challenge(string token, out string? keyAuthorization) =>
        _http01Challenges.TryGetValue(token, out keyAuthorization);

    public bool TryGetTlsAlpnChallengeCertificate(string sniHost, out X509Certificate2? certificate) =>
        _tlsAlpnChallenges.TryGetValue(sniHost.Trim(), out certificate);

    public async ValueTask EnsureAllDomainsProvisionedAsync(ConfigSnapshot snapshot, CancellationToken ct)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        var pendingDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (snapshot.Server.Features.TlsPolicy.Acme.Enabled &&
            snapshot.Server.Features.TlsPolicy.Acme.AcceptTermsOfService)
        {
            foreach (string domain in snapshot.Server.Features.TlsPolicy.Acme.Domains)
            {
                if (!string.IsNullOrWhiteSpace(domain))
                {
                    pendingDomains.Add(domain.Trim());
                }
            }
        }

        foreach (SiteConfig site in snapshot.Sites.Values)
        {
            if (site.DesiredState != DesiredResourceState.Started ||
                !site.Features.TlsPolicy.Acme.Enabled ||
                !site.Features.TlsPolicy.Acme.AcceptTermsOfService)
            {
                continue;
            }

            AcmePolicyConfig acme = site.Features.TlsPolicy.Acme;
            if (acme.Domains.Count > 0)
            {
                foreach (string d in acme.Domains)
                {
                    if (!string.IsNullOrWhiteSpace(d))
                    {
                        pendingDomains.Add(d.Trim());
                    }
                }
            }
            else
            {
                foreach (SiteBindingConfig binding in site.Bindings)
                {
                    if (string.Equals(binding.Protocol, "https", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(binding.Host) &&
                        binding.Host != "*" &&
                        !string.Equals(binding.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                    {
                        pendingDomains.Add(binding.Host.Trim());
                    }
                }
            }

            foreach (string domain in pendingDomains.ToArray())
            {
                if (_certificateProvider.NeedsAcmeProvisionOrRenewal(domain, acme.RenewBeforeDays, now, _layout.CertsConfigDirectory))
                {
                    await ProvisionDomainCertificateAsync(domain, acme, ct).ConfigureAwait(false);
                }
            }
            pendingDomains.Clear();
        }

        if (pendingDomains.Count > 0)
        {
            AcmePolicyConfig srvAcme = snapshot.Server.Features.TlsPolicy.Acme;
            foreach (string domain in pendingDomains)
            {
                if (_certificateProvider.NeedsAcmeProvisionOrRenewal(domain, srvAcme.RenewBeforeDays, now, _layout.CertsConfigDirectory))
                {
                    await ProvisionDomainCertificateAsync(domain, srvAcme, ct).ConfigureAwait(false);
                }
            }
        }
    }

    public async ValueTask<X509Certificate2> ProvisionDomainCertificateAsync(
        string domain,
        AcmePolicyConfig policy,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        ArgumentNullException.ThrowIfNull(policy);

        await _renewLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_layout.CertsConfigDirectory);

            // 1. Fetch ACME Directory
            using HttpResponseMessage dirResp = await _httpClient.GetAsync(policy.DirectoryUrl, ct).ConfigureAwait(false);
            dirResp.EnsureSuccessStatusCode();
            string dirJson = await dirResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using JsonDocument dirDoc = JsonDocument.Parse(dirJson);
            string newNonceUrl = dirDoc.RootElement.GetProperty("newNonce").GetString()!;
            string newAccountUrl = dirDoc.RootElement.GetProperty("newAccount").GetString()!;
            string newOrderUrl = dirDoc.RootElement.GetProperty("newOrder").GetString()!;

            string nonce = await FetchNonceAsync(newNonceUrl, ct).ConfigureAwait(false);

            // 2. Load or Create Account Key (ES256 P-256)
            (AcmeAccountRecord accountRecord, ECDsa accountKey) = await LoadOrCreateAccountAsync(ct).ConfigureAwait(false);
            try
            {
                if (string.IsNullOrWhiteSpace(accountRecord.AccountUri))
                {
                    string contactJson = string.IsNullOrWhiteSpace(policy.ContactEmail)
                        ? "[]"
                        : $"[{JsonSerializer.Serialize($"mailto:{policy.ContactEmail.Trim()}", KiteJsonContext.Default.String)}]";
                    string acctPayload = $"{{\"termsOfServiceAgreed\":{(policy.AcceptTermsOfService ? "true" : "false")},\"contact\":{contactJson}}}";

                    (string _, string? location, string nextNonce) = await SendSignedPostAsync(
                        newAccountUrl,
                        acctPayload,
                        nonce,
                        accountKey,
                        kid: null,
                        ct).ConfigureAwait(false);

                    nonce = nextNonce;
                    accountRecord.AccountUri = location ?? newAccountUrl;
                    await AtomicJsonStore.WriteAtomicAsync(
                        _layout.AcmeAccountPath,
                        accountRecord,
                        KiteJsonContext.Default.AcmeAccountRecord,
                        ct).ConfigureAwait(false);
                }

                string thumbprint = ComputeJwkThumbprint(accountKey);

                // 3. Create ACME Order
                string orderPayload = $"{{\"identifiers\":[{{\"type\":\"dns\",\"value\":{JsonSerializer.Serialize(domain, KiteJsonContext.Default.String)}}}]}}";
                (string orderJson, string? orderLocation, string orderNonce) = await SendSignedPostAsync(
                    newOrderUrl,
                    orderPayload,
                    nonce,
                    accountKey,
                    accountRecord.AccountUri,
                    ct).ConfigureAwait(false);
                nonce = orderNonce;

                using JsonDocument orderDoc = JsonDocument.Parse(orderJson);
                string finalizeUrl = orderDoc.RootElement.GetProperty("finalize").GetString()!;
                string authUrl = orderDoc.RootElement.GetProperty("authorizations")[0].GetString()!;

                // 4. Fetch Authorization & Activate HTTP-01 or TLS-ALPN-01 Challenge
                (string authJson, string? _, string authNonce) = await SendSignedPostAsync(
                    authUrl,
                    payloadJson: "",
                    nonce,
                    accountKey,
                    accountRecord.AccountUri,
                    ct).ConfigureAwait(false);
                nonce = authNonce;

                using JsonDocument authDoc = JsonDocument.Parse(authJson);
                string preferredType = policy.ChallengeType switch
                {
                    AcmeChallengeType.TlsAlpn01 => "tls-alpn-01",
                    AcmeChallengeType.Dns01 => "dns-01",
                    _ => "http-01"
                };
                JsonElement? selectedChallenge = null;
                foreach (JsonElement ch in authDoc.RootElement.GetProperty("challenges").EnumerateArray())
                {
                    string? chType = ch.GetProperty("type").GetString();
                    if (string.Equals(chType, preferredType, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedChallenge = ch;
                        break;
                    }
                    selectedChallenge ??= ch;
                }

                JsonElement challenge = selectedChallenge ?? throw new InvalidOperationException("No ACME challenge offered by directory.");
                string challengeUrl = challenge.GetProperty("url").GetString()!;
                string token = challenge.GetProperty("token").GetString()!;
                string chosenType = challenge.GetProperty("type").GetString()!;
                string keyAuthorization = $"{token}.{thumbprint}";
                string? dnsTxtValue = null;

                if (string.Equals(chosenType, "tls-alpn-01", StringComparison.OrdinalIgnoreCase))
                {
                    X509Certificate2 alpnCert = CreateTlsAlpnChallengeCertificate(domain, keyAuthorization);
                    _tlsAlpnChallenges[domain] = alpnCert;
                }
                else if (string.Equals(chosenType, "dns-01", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] txtDigest = SHA256.HashData(Encoding.UTF8.GetBytes(keyAuthorization));
                    dnsTxtValue = Base64Url.EncodeToString(txtDigest);
                    await PublishOrDeleteDns01TxtAsync(policy, domain, dnsTxtValue, create: true, ct).ConfigureAwait(false);
                }
                else
                {
                    _http01Challenges[token] = keyAuthorization;
                }

                try
                {
                    (string _, string? _, string chNonce) = await SendSignedPostAsync(
                        challengeUrl,
                        payloadJson: "{}",
                        nonce,
                        accountKey,
                        accountRecord.AccountUri,
                        ct).ConfigureAwait(false);
                    nonce = chNonce;

                    // 5. Poll Authorization / Order until ready or valid
                    for (int attempt = 0; attempt < 20; attempt++)
                    {
                        (string pollJson, string? _, string pollNonce) = await SendSignedPostAsync(
                            authUrl,
                            payloadJson: "",
                            nonce,
                            accountKey,
                            accountRecord.AccountUri,
                            ct).ConfigureAwait(false);
                        nonce = pollNonce;

                        using JsonDocument pollDoc = JsonDocument.Parse(pollJson);
                        string? status = pollDoc.RootElement.GetProperty("status").GetString();
                        if (string.Equals(status, "valid", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }
                        if (string.Equals(status, "invalid", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException($"ACME challenge validation failed for '{domain}'.");
                        }

                        await Task.Delay(250, ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _http01Challenges.TryRemove(token, out _);
                    if (_tlsAlpnChallenges.TryRemove(domain, out X509Certificate2? oldAlpn))
                    {
                        oldAlpn.Dispose();
                    }
                    if (dnsTxtValue is not null)
                    {
                        try
                        {
                            await PublishOrDeleteDns01TxtAsync(policy, domain, dnsTxtValue, create: false, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch
                        {
                        }
                    }
                }

                // 6. Generate RSA-2048 Domain Key + CSR & Finalize Order
                using var certKey = RSA.Create(2048);
                var csr = new CertificateRequest($"CN={domain}", certKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var sanBuilder = new SubjectAlternativeNameBuilder();
                sanBuilder.AddDnsName(domain);
                csr.CertificateExtensions.Add(sanBuilder.Build());

                byte[] csrDer = csr.CreateSigningRequest();
                string csrB64 = Base64Url.EncodeToString(csrDer);

                (string finalJson, string? _, string finalNonce) = await SendSignedPostAsync(
                    finalizeUrl,
                    $"{{\"csr\":\"{csrB64}\"}}",
                    nonce,
                    accountKey,
                    accountRecord.AccountUri,
                    ct).ConfigureAwait(false);
                nonce = finalNonce;

                using JsonDocument finalDoc = JsonDocument.Parse(finalJson);
                string certUrl = finalDoc.RootElement.TryGetProperty("certificate", out JsonElement certProp)
                    ? certProp.GetString()!
                    : (orderLocation ?? throw new InvalidOperationException("ACME finalize response missing certificate URL."));

                (string pemChain, string? _, _) = await SendSignedPostAsync(
                    certUrl,
                    payloadJson: "",
                    nonce,
                    accountKey,
                    accountRecord.AccountUri,
                    ct).ConfigureAwait(false);

                using X509Certificate2 leafNoKey = X509Certificate2.CreateFromPem(pemChain);
                using X509Certificate2 leafWithKey = leafNoKey.CopyWithPrivateKey(certKey);
                byte[] pfxBytes = leafWithKey.Export(X509ContentType.Pfx, "kite-acme");

                string pfxPath = _layout.GetDomainPfxPath(domain);
                await AtomicJsonStore.WriteBytesAtomicAsync(pfxPath, pfxBytes, ct).ConfigureAwait(false);

                X509Certificate2 installed = X509CertificateLoader.LoadPkcs12(
                    pfxBytes,
                    "kite-acme",
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);

                _certificateProvider.InstallOrRotateCertificate(domain, installed, "AcmeAuto");
                return installed;
            }
            finally
            {
                accountKey.Dispose();
            }
        }
        finally
        {
            _renewLock.Release();
        }
    }

    public static string ComputeJwkThumbprint(ECDsa key)
    {
        ECParameters ec = key.ExportParameters(includePrivateParameters: false);
        string x = Base64Url.EncodeToString(ec.Q.X!);
        string y = Base64Url.EncodeToString(ec.Q.Y!);
        string canonicalJwk = $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{x}\",\"y\":\"{y}\"}}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJwk));
        return Base64Url.EncodeToString(hash);
    }

    public static string BuildFlattenedJws(
        string url,
        string payloadJson,
        string nonce,
        ECDsa key,
        string? kid)
    {
        string protectedJson;
        if (!string.IsNullOrEmpty(kid))
        {
            protectedJson = $"{{\"alg\":\"ES256\",\"kid\":{JsonSerializer.Serialize(kid, KiteJsonContext.Default.String)},\"nonce\":{JsonSerializer.Serialize(nonce, KiteJsonContext.Default.String)},\"url\":{JsonSerializer.Serialize(url, KiteJsonContext.Default.String)}}}";
        }
        else
        {
            ECParameters ec = key.ExportParameters(includePrivateParameters: false);
            string x = Base64Url.EncodeToString(ec.Q.X!);
            string y = Base64Url.EncodeToString(ec.Q.Y!);
            protectedJson = $"{{\"alg\":\"ES256\",\"jwk\":{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{x}\",\"y\":\"{y}\"}},\"nonce\":{JsonSerializer.Serialize(nonce, KiteJsonContext.Default.String)},\"url\":{JsonSerializer.Serialize(url, KiteJsonContext.Default.String)}}}";
        }

        string protectedB64 = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(protectedJson));
        string payloadB64 = string.IsNullOrEmpty(payloadJson)
            ? ""
            : Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payloadJson));

        byte[] signingInput = Encoding.ASCII.GetBytes($"{protectedB64}.{payloadB64}");
        byte[] sigBytes = key.SignData(signingInput, HashAlgorithmName.SHA256);
        string sigB64 = Base64Url.EncodeToString(sigBytes);

        return $"{{\"protected\":\"{protectedB64}\",\"payload\":\"{payloadB64}\",\"signature\":\"{sigB64}\"}}";
    }

    public static X509Certificate2 CreateTlsAlpnChallengeCertificate(string domain, string keyAuthorization)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={domain}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(domain);
        req.CertificateExtensions.Add(san.Build());

        // RFC 8737: id-pe-acmeIdentifier (1.3.6.1.5.5.7.1.31), critical DER OCTET STRING of SHA256(keyAuthorization)
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(keyAuthorization));
        byte[] derOctet = new byte[digest.Length + 2];
        derOctet[0] = 0x04;
        derOctet[1] = (byte)digest.Length;
        digest.CopyTo(derOctet, 2);
        req.CertificateExtensions.Add(new X509Extension(new Oid("1.3.6.1.5.5.7.1.31"), derOctet, critical: true));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 cert = req.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(1));
        byte[] pfx = cert.Export(X509ContentType.Pfx, "kite-alpn");
        return X509CertificateLoader.LoadPkcs12(pfx, "kite-alpn", X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
    }

    private async Task<string> FetchNonceAsync(string newNonceUrl, CancellationToken ct)
    {
        using var headReq = new HttpRequestMessage(HttpMethod.Head, newNonceUrl);
        using HttpResponseMessage resp = await _httpClient.SendAsync(headReq, ct).ConfigureAwait(false);
        if (resp.Headers.TryGetValues("Replay-Nonce", out IEnumerable<string>? values))
        {
            return values.First();
        }
        return Guid.NewGuid().ToString("N");
    }

    private async Task<(string Body, string? Location, string NextNonce)> SendSignedPostAsync(
        string url,
        string payloadJson,
        string nonce,
        ECDsa key,
        string? kid,
        CancellationToken ct)
    {
        string jws = BuildFlattenedJws(url, payloadJson, nonce, key, kid);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jws, Encoding.UTF8, new MediaTypeHeaderValue("application/jose+json"))
        };

        using HttpResponseMessage resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        string nextNonce = resp.Headers.TryGetValues("Replay-Nonce", out IEnumerable<string>? nonceVals)
            ? nonceVals.First()
            : nonce;
        string? location = resp.Headers.Location?.ToString();
        return (body, location, nextNonce);
    }

    private async Task<(AcmeAccountRecord Record, ECDsa Key)> LoadOrCreateAccountAsync(CancellationToken ct)
    {
        if (File.Exists(_layout.AcmeAccountPath))
        {
            AcmeAccountRecord? existing = await AtomicJsonStore.TryReadAsync(
                _layout.AcmeAccountPath,
                KiteJsonContext.Default.AcmeAccountRecord,
                ct).ConfigureAwait(false);
            if (existing is not null && !string.IsNullOrWhiteSpace(existing.PrivateKeyPkcs8Base64))
            {
                var loaded = ECDsa.Create();
                loaded.ImportPkcs8PrivateKey(Convert.FromBase64String(existing.PrivateKeyPkcs8Base64), out _);
                return (existing, loaded);
            }
        }

        var freshKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var record = new AcmeAccountRecord
        {
            PrivateKeyPkcs8Base64 = Convert.ToBase64String(freshKey.ExportPkcs8PrivateKey())
        };
        await AtomicJsonStore.WriteAtomicAsync(_layout.AcmeAccountPath, record, KiteJsonContext.Default.AcmeAccountRecord, ct).ConfigureAwait(false);
        return (record, freshKey);
    }

    /// <summary>
    /// Evaluates whether an unknown SNI hostname is permitted for On-Demand TLS certificate issuance
    /// against configured host suffixes and optional external validation webhook.
    /// </summary>
    public async ValueTask<bool> IsOnDemandSniAllowedAsync(
        string sniHost,
        OnDemandTlsConfig policy,
        CancellationToken ct = default)
    {
        if (!policy.Enabled || string.IsNullOrWhiteSpace(sniHost))
        {
            return false;
        }

        string host = sniHost.Trim();
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (_onDemandHostCache.TryGetValue(host, out var cached) && cached.ExpiresUtc > now)
        {
            return cached.Allowed;
        }

        bool allowed = false;
        if (policy.AllowedHostSuffixes.Count > 0)
        {
            foreach (string suffix in policy.AllowedHostSuffixes)
            {
                if (string.IsNullOrWhiteSpace(suffix))
                {
                    continue;
                }

                string clean = suffix.Trim().TrimStart('*');
                if (host.EndsWith(clean, StringComparison.OrdinalIgnoreCase))
                {
                    allowed = true;
                    break;
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(policy.AskEndpointUrl))
        {
            try
            {
                string sep = policy.AskEndpointUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
                string askUri = $"{policy.AskEndpointUrl}{sep}domain={Uri.EscapeDataString(host)}";
                using HttpResponseMessage resp = await _httpClient.GetAsync(askUri, ct).ConfigureAwait(false);
                allowed = resp.IsSuccessStatusCode;
            }
            catch
            {
                allowed = false;
            }
        }

        _onDemandHostCache[host] = (allowed, now.AddSeconds(Math.Clamp(policy.CacheTtlSeconds, 10, 86_400)));
        return allowed;
    }

    private async Task PublishOrDeleteDns01TxtAsync(
        AcmePolicyConfig policy,
        string domain,
        string txtValue,
        bool create,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(policy.DnsWebhookUrl))
        {
            throw new InvalidOperationException("AcmePolicyConfig.DnsWebhookUrl is required for DNS-01 challenges.");
        }

        string recordName = $"_acme-challenge.{domain.Trim().TrimStart('.')}";
        string jsonPayload = $"{{\"domain\":{JsonSerializer.Serialize(recordName, KiteJsonContext.Default.String)},\"value\":{JsonSerializer.Serialize(txtValue, KiteJsonContext.Default.String)}}}";

        using var req = new HttpRequestMessage(create ? HttpMethod.Post : HttpMethod.Delete, policy.DnsWebhookUrl)
        {
            Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(policy.DnsWebhookAuthToken))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", policy.DnsWebhookAuthToken);
        }

        using HttpResponseMessage resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    public void Dispose()
    {
        _renewLock.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
        foreach (X509Certificate2 c in _tlsAlpnChallenges.Values)
        {
            c.Dispose();
        }
        _tlsAlpnChallenges.Clear();
    }
}
