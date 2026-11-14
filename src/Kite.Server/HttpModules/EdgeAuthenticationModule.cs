using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kite.Core.Models;
using Kite.Core.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Kite.Server.HttpModules;

/// <summary>
/// Edge authentication module validating Anonymous access, API Keys (<c>X-API-Key</c>),
/// HTTP Basic credentials (SHA-256 or constant-time digest), and JWT Bearer tokens (<c>HS256</c>/<c>RS256</c>)
/// directly in the Kestrel pipeline.
/// </summary>
public static class EdgeAuthenticationModule
{
    public static bool Authenticate(
        HttpContext context,
        CompiledSiteFeatures compiled,
        TimeProvider timeProvider,
        out string? authenticatedPrincipal,
        out string? wwwAuthenticateChallenge,
        out string? failureDetail)
    {
        AuthenticationConfig auth = compiled.EffectiveConfig.Authentication;
        authenticatedPrincipal = null;
        wwwAuthenticateChallenge = null;
        failureDetail = null;

        bool hasAuthHeader = context.Request.Headers.TryGetValue("Authorization", out StringValues authHeaderValues) &&
                             !StringValues.IsNullOrEmpty(authHeaderValues);
        StringValues apiKeyValues = default;
        bool hasApiKeyHeader = auth.ApiKeyEnabled &&
                               context.Request.Headers.TryGetValue(auth.ApiKeyHeaderName, out apiKeyValues) &&
                               !StringValues.IsNullOrEmpty(apiKeyValues);

        // If Anonymous Auth is enabled and client did not supply explicit credentials, pass through.
        if (auth.AnonymousEnabled && !hasAuthHeader && !hasApiKeyHeader)
        {
            authenticatedPrincipal = "IUSR";
            return true;
        }

        // 1. API Key check
        if (auth.ApiKeyEnabled && hasApiKeyHeader)
        {
            string presentedKey = apiKeyValues.ToString().Trim();
            if (compiled.AllowedApiKeys.Contains(presentedKey))
            {
                authenticatedPrincipal = "ApiKeyClient";
                context.Request.Headers["X-Kite-Auth-Scheme"] = "ApiKey";
                context.Request.Headers["X-Kite-Auth-Principal"] = authenticatedPrincipal;
                return true;
            }

            failureDetail = $"Presented API key in header '{auth.ApiKeyHeaderName}' was not recognized.";
        }

        if (hasAuthHeader)
        {
            string authHeader = authHeaderValues.ToString().Trim();

            // 2. JWT Bearer Validation (HS256 / RS256)
            if (auth.JwtEnabled && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                string token = authHeader["Bearer ".Length..].Trim();
                if (TryValidateJwt(token, compiled, auth, timeProvider, out string? sub, out string? jwtErr))
                {
                    authenticatedPrincipal = sub ?? "JwtPrincipal";
                    context.Request.Headers["X-Kite-Auth-Scheme"] = "Bearer";
                    context.Request.Headers["X-Kite-Auth-Principal"] = authenticatedPrincipal;
                    return true;
                }

                wwwAuthenticateChallenge = "Bearer error=\"invalid_token\"";
                failureDetail = jwtErr ?? "JWT bearer validation failed.";
                return false;
            }

            // 3. Basic Auth Validation
            if (auth.BasicAuthEnabled && authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                string b64 = authHeader["Basic ".Length..].Trim();
                if (TryValidateBasicAuth(b64, auth, out string? user))
                {
                    authenticatedPrincipal = user;
                    context.Request.Headers["X-Kite-Auth-Scheme"] = "Basic";
                    context.Request.Headers["X-Kite-Auth-Principal"] = user!;
                    return true;
                }

                wwwAuthenticateChallenge = $"Basic realm=\"{auth.BasicAuthRealm}\"";
                failureDetail = "Invalid Basic authentication username or password.";
                return false;
            }
        }

        // If anonymous access is allowed AND no specific invalid credential was attempted for an enabled provider
        if (auth.AnonymousEnabled && failureDetail is null)
        {
            authenticatedPrincipal = "IUSR";
            return true;
        }

        wwwAuthenticateChallenge ??= auth.JwtEnabled
            ? "Bearer realm=\"Kite\""
            : (auth.BasicAuthEnabled ? $"Basic realm=\"{auth.BasicAuthRealm}\"" : "ApiKey");
        failureDetail ??= "Authentication is required to access this resource (401 Unauthorized).";
        return false;
    }

    private static bool TryValidateBasicAuth(string base64Token, AuthenticationConfig auth, out string? username)
    {
        username = null;
        try
        {
            byte[] rawBytes = Convert.FromBase64String(base64Token);
            string decoded = Encoding.UTF8.GetString(rawBytes);
            int colon = decoded.IndexOf(':');
            if (colon <= 0)
            {
                return false;
            }

            string user = decoded[..colon];
            string pass = decoded[(colon + 1)..];
            if (!auth.BasicAuthUsers.TryGetValue(user, out string? expected))
            {
                return false;
            }

            // Support either sha256 hex (64 hex chars / "sha256:<hex>") or plain text in local dev
            if (expected.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                expected = expected["sha256:".Length..];
            }

            if (expected.Length == 64 && expected.All(Uri.IsHexDigit))
            {
                Span<byte> hash = stackalloc byte[32];
                SHA256.HashData(Encoding.UTF8.GetBytes(pass), hash);
                string hex = Convert.ToHexString(hash);
                if (string.Equals(hex, expected, StringComparison.OrdinalIgnoreCase))
                {
                    username = user;
                    return true;
                }
                return false;
            }

            if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(pass), Encoding.UTF8.GetBytes(expected)))
            {
                username = user;
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryValidateJwt(
        string token,
        CompiledSiteFeatures compiled,
        AuthenticationConfig auth,
        TimeProvider timeProvider,
        out string? subject,
        out string? error)
    {
        subject = null;
        error = null;

        int dot1 = token.IndexOf('.');
        if (dot1 <= 0)
        {
            error = "Malformed JWT format.";
            return false;
        }

        int dot2 = token.IndexOf('.', dot1 + 1);
        if (dot2 <= dot1 + 1 || dot2 >= token.Length - 1)
        {
            error = "JWT signature segment missing.";
            return false;
        }

        string headerSeg = token[..dot1];
        string payloadSeg = token[(dot1 + 1)..dot2];
        string sigSeg = token[(dot2 + 1)..];
        byte[] signingInput = Encoding.ASCII.GetBytes(token[..dot2]);

        byte[] signatureBytes;
        try
        {
            signatureBytes = DecodeBase64Url(sigSeg);
        }
        catch
        {
            error = "JWT signature is not valid Base64Url.";
            return false;
        }

        // Verify cryptographic signature
        bool sigValid = false;
        if (string.Equals(auth.JwtSigningAlgorithm, "RS256", StringComparison.OrdinalIgnoreCase))
        {
            if (compiled.JwtRsaPublicKey is null)
            {
                error = "RS256 JWT enabled but RSA public key is not configured.";
                return false;
            }

            sigValid = compiled.JwtRsaPublicKey.VerifyData(signingInput, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        else
        {
            if (compiled.JwtSymmetricKeyBytes is null || compiled.JwtSymmetricKeyBytes.Length == 0)
            {
                error = "HS256 JWT enabled but signing secret is not configured.";
                return false;
            }

            Span<byte> computed = stackalloc byte[32];
            HMACSHA256.HashData(compiled.JwtSymmetricKeyBytes, signingInput, computed);
            sigValid = CryptographicOperations.FixedTimeEquals(computed, signatureBytes);
        }

        if (!sigValid)
        {
            error = "JWT signature verification failed.";
            return false;
        }

        // Validate header alg + JSON payload claims
        try
        {
            byte[] headerBytes = DecodeBase64Url(headerSeg);
            using JsonDocument headerDoc = JsonDocument.Parse(headerBytes);
            if (headerDoc.RootElement.TryGetProperty("alg", out JsonElement algEl))
            {
                string? alg = algEl.GetString();
                if (!string.Equals(alg, auth.JwtSigningAlgorithm, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"JWT 'alg' ({alg}) does not match configured '{auth.JwtSigningAlgorithm}'.";
                    return false;
                }
            }

            byte[] payloadBytes = DecodeBase64Url(payloadSeg);
            using JsonDocument payloadDoc = JsonDocument.Parse(payloadBytes);
            JsonElement root = payloadDoc.RootElement;
            long nowEpoch = timeProvider.GetUtcNow().ToUnixTimeSeconds();

            if (root.TryGetProperty("exp", out JsonElement expEl) && expEl.TryGetInt64(out long exp) && nowEpoch >= exp)
            {
                error = "JWT token has expired.";
                return false;
            }

            if (root.TryGetProperty("nbf", out JsonElement nbfEl) && nbfEl.TryGetInt64(out long nbf) && nowEpoch < nbf)
            {
                error = "JWT token is not yet valid (nbf).";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(auth.JwtIssuer))
            {
                string? iss = root.TryGetProperty("iss", out JsonElement issEl) ? issEl.GetString() : null;
                if (!string.Equals(iss, auth.JwtIssuer, StringComparison.Ordinal))
                {
                    error = $"JWT issuer mismatch (expected '{auth.JwtIssuer}').";
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(auth.JwtAudience))
            {
                bool audOk = false;
                if (root.TryGetProperty("aud", out JsonElement audEl))
                {
                    if (audEl.ValueKind == JsonValueKind.String)
                    {
                        audOk = string.Equals(audEl.GetString(), auth.JwtAudience, StringComparison.Ordinal);
                    }
                    else if (audEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement item in audEl.EnumerateArray())
                        {
                            if (string.Equals(item.GetString(), auth.JwtAudience, StringComparison.Ordinal))
                            {
                                audOk = true;
                                break;
                            }
                        }
                    }
                }

                if (!audOk)
                {
                    error = $"JWT audience mismatch (expected '{auth.JwtAudience}').";
                    return false;
                }
            }

            subject = root.TryGetProperty("sub", out JsonElement subEl) ? subEl.GetString() : "JwtUser";
            return true;
        }
        catch (JsonException)
        {
            error = "Invalid JWT JSON payload.";
            return false;
        }
    }

    public static byte[] DecodeBase64Url(string segment)
    {
        string padded = segment.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }

    public static string EncodeBase64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static readonly HttpClient ForwardAuthHttpClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    });

    /// <summary>
    /// Evaluates external <c>ext_authz</c> / ForwardAuth policy against a configured HTTP endpoint,
    /// propagating configured identity headers (e.g. <c>X-Auth-User</c>, <c>X-Auth-Roles</c>) downstream on HTTP 2xx.
    /// </summary>
    public static async ValueTask<(bool Allowed, int StatusCode, string? WwwAuthenticate, string? FailureDetail)> EvaluateForwardAuthAsync(
        HttpContext context,
        ForwardAuthConfig config,
        HttpClient? httpClientOverride = null,
        CancellationToken ct = default)
    {
        if (!config.Enabled || string.IsNullOrWhiteSpace(config.EndpointUrl))
        {
            return (true, StatusCodes.Status200OK, null, null);
        }

        HttpClient client = httpClientOverride ?? ForwardAuthHttpClient;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Math.Clamp(config.TimeoutMilliseconds, 100, 30_000));

        try
        {
            using var subReq = new HttpRequestMessage(HttpMethod.Get, config.EndpointUrl);
            subReq.Headers.TryAddWithoutValidation("X-Forwarded-Method", context.Request.Method);
            subReq.Headers.TryAddWithoutValidation("X-Forwarded-Uri", $"{context.Request.Path}{context.Request.QueryString}");
            subReq.Headers.TryAddWithoutValidation("X-Forwarded-Host", context.Request.Host.Value ?? "localhost");
            subReq.Headers.TryAddWithoutValidation("X-Forwarded-Proto", context.Request.Scheme);

            foreach (string headerName in config.CopyRequestHeaders)
            {
                if (!string.IsNullOrWhiteSpace(headerName) &&
                    context.Request.Headers.TryGetValue(headerName, out StringValues vals))
                {
                    subReq.Headers.TryAddWithoutValidation(headerName, vals.ToArray());
                }
            }

            using HttpResponseMessage resp = await client.SendAsync(subReq, cts.Token).ConfigureAwait(false);
            if ((int)resp.StatusCode is >= 200 and < 300)
            {
                foreach (string respHeader in config.CopyResponseHeaders)
                {
                    if (!string.IsNullOrWhiteSpace(respHeader) &&
                        resp.Headers.TryGetValues(respHeader, out IEnumerable<string>? values))
                    {
                        context.Request.Headers[respHeader] = values.ToArray();
                    }
                }
                return (true, StatusCodes.Status200OK, null, null);
            }

            string? wwwAuth = resp.Headers.TryGetValues("WWW-Authenticate", out IEnumerable<string>? authVals)
                ? string.Join(", ", authVals)
                : null;
            int rejectStatus = (int)resp.StatusCode == StatusCodes.Status403Forbidden
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status401Unauthorized;
            return (false, rejectStatus, wwwAuth, $"ForwardAuth subrequest rejected request with HTTP {(int)resp.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (config.FailOpen)
            {
                return (true, StatusCodes.Status200OK, null, null);
            }

            return (false, StatusCodes.Status401Unauthorized, null, $"ForwardAuth endpoint unavailable: {ex.Message}");
        }
    }
}
