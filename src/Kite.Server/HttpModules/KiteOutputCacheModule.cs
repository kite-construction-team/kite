using System.Collections.Concurrent;
using Kite.Core.Memory;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Kite.Server.HttpModules;

/// <summary>
/// Implements RFC 9111 (HTTP Caching) server-side output caching backed by LOH-free <see cref="PooledBytePayload"/> segments.
/// Supports <c>Cache-Control</c> directives, <c>Age</c> header calculation, <c>Vary: Accept-Encoding</c>,
/// conditional <c>304 Not Modified</c> revalidation, and cache invalidation on unsafe methods (POST/PUT/DELETE/PATCH).
/// </summary>
public sealed class KiteOutputCacheModule : IDisposable
{
    private readonly ConcurrentDictionary<string, CachedResponseEntry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _siteGenerations = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;
    private long _totalCachedBytes;
    private long _cacheHits;
    private long _cacheMisses;

    public KiteOutputCacheModule(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public int Count => _entries.Count;
    public long TotalCachedBytes => Math.Max(0, Interlocked.Read(ref _totalCachedBytes));
    public long CacheHits => Interlocked.Read(ref _cacheHits);
    public long CacheMisses => Interlocked.Read(ref _cacheMisses);

    public double HitRatioPercent
    {
        get
        {
            long hits = CacheHits;
            long total = hits + CacheMisses;
            return total == 0 ? 0.0 : Math.Round((hits * 100.0) / total, 1);
        }
    }

    public void EnsureGeneration(string siteId, long flushGeneration)
    {
        long previous = _siteGenerations.AddOrUpdate(siteId, flushGeneration, static (_, next) => next);
        if (flushGeneration > previous)
        {
            FlushSite(siteId);
            _siteGenerations[siteId] = flushGeneration;
        }
    }

    public void FlushSite(string siteId)
    {
        string prefix = $"{siteId}|";
        foreach (string key in _entries.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                _entries.TryRemove(key, out CachedResponseEntry? removed))
            {
                Interlocked.Add(ref _totalCachedBytes, -removed.Payload.Length);
                removed.Dispose();
            }
        }
    }

    public void FlushAll()
    {
        foreach (string key in _entries.Keys)
        {
            if (_entries.TryRemove(key, out CachedResponseEntry? removed))
            {
                Interlocked.Add(ref _totalCachedBytes, -removed.Payload.Length);
                removed.Dispose();
            }
        }
    }

    public static bool RequestAllowsCacheLookup(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            return false;
        }

        if (request.Headers.TryGetValue("Cache-Control", out StringValues cc))
        {
            string ccText = cc.ToString();
            if (ccText.Contains("no-cache", StringComparison.OrdinalIgnoreCase) ||
                ccText.Contains("no-store", StringComparison.OrdinalIgnoreCase) ||
                ccText.Contains("max-age=0", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (request.Headers.TryGetValue("Pragma", out StringValues pragma) &&
            pragma.ToString().Contains("no-cache", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Do not serve full cached body directly if client sent a byte-range request
        if (request.Headers.ContainsKey("Range"))
        {
            return false;
        }

        return true;
    }

    public bool TryGetFreshEntry(
        string siteId,
        HttpRequest request,
        string negotiatedEncoding,
        out CachedResponseEntry? entry,
        out int ageSeconds)
    {
        string key = BuildCacheKey(siteId, request.Path.Value ?? "/", request.QueryString.Value ?? "", negotiatedEncoding);
        if (_entries.TryGetValue(key, out entry))
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (now < entry.ExpiresAtUtc)
            {
                Interlocked.Increment(ref _cacheHits);
                ageSeconds = Math.Max(0, (int)(now - entry.StoredAtUtc).TotalSeconds);
                return true;
            }

            if (_entries.TryRemove(key, out CachedResponseEntry? expired))
            {
                Interlocked.Add(ref _totalCachedBytes, -expired.Payload.Length);
                expired.Dispose();
            }
        }

        Interlocked.Increment(ref _cacheMisses);
        entry = null;
        ageSeconds = 0;
        return false;
    }

    public void StoreResponse(
        string siteId,
        HttpRequest request,
        string negotiatedEncoding,
        int statusCode,
        IHeaderDictionary responseHeaders,
        PooledBytePayload payload,
        int defaultMaxAgeSeconds,
        int maxCacheSizeMegabytes = 500)
    {
        if (statusCode != StatusCodes.Status200OK)
        {
            payload.Dispose();
            return;
        }

        long maxTotalBytes = Math.Max(1L, maxCacheSizeMegabytes) * 1024L * 1024L;
        if (TotalCachedBytes + payload.Length > maxTotalBytes)
        {
            payload.Dispose();
            return;
        }

        int maxAgeSeconds = defaultMaxAgeSeconds;
        if (responseHeaders.TryGetValue("Cache-Control", out StringValues ccVal))
        {
            string cc = ccVal.ToString();
            if (cc.Contains("no-store", StringComparison.OrdinalIgnoreCase) ||
                cc.Contains("private", StringComparison.OrdinalIgnoreCase) ||
                cc.Contains("no-cache", StringComparison.OrdinalIgnoreCase))
            {
                payload.Dispose();
                return;
            }

            if (TryExtractDirectiveSeconds(cc.AsSpan(), "s-maxage=", out int sMaxAge))
            {
                maxAgeSeconds = sMaxAge;
            }
            else if (TryExtractDirectiveSeconds(cc.AsSpan(), "max-age=", out int parsedMaxAge))
            {
                maxAgeSeconds = parsedMaxAge;
            }
        }

        if (maxAgeSeconds <= 0)
        {
            payload.Dispose();
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        var copiedHeaders = new List<KeyValuePair<string, string>>(responseHeaders.Count);
        string? etag = null;

        foreach (KeyValuePair<string, StringValues> kvp in responseHeaders)
        {
            if (string.Equals(kvp.Key, "Age", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kvp.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kvp.Key, "Connection", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string val = kvp.Value.ToString();
            if (string.Equals(kvp.Key, "ETag", StringComparison.OrdinalIgnoreCase))
            {
                etag = val;
            }

            copiedHeaders.Add(new KeyValuePair<string, string>(kvp.Key, val));
        }

        etag ??= ConditionalAndRangeEvaluator.GenerateStrongETag(now, payload.Length);

        var newEntry = new CachedResponseEntry(
            statusCode,
            copiedHeaders,
            etag,
            now,
            now.AddSeconds(maxAgeSeconds),
            payload);

        string key = BuildCacheKey(siteId, request.Path.Value ?? "/", request.QueryString.Value ?? "", negotiatedEncoding);
        if (_entries.TryRemove(key, out CachedResponseEntry? old))
        {
            Interlocked.Add(ref _totalCachedBytes, -old.Payload.Length);
            old.Dispose();
        }

        _entries[key] = newEntry;
        Interlocked.Add(ref _totalCachedBytes, payload.Length);
    }

    /// <summary>
    /// RFC 9111 Section 4.4: Unsafe methods (POST, PUT, DELETE, PATCH) MUST invalidate cached entries for the target URI.
    /// </summary>
    public void InvalidateByUri(string siteId, string path, string queryString)
    {
        string prefix = $"{siteId}|{path}|{queryString}|";
        foreach (string key in _entries.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                if (_entries.TryRemove(key, out CachedResponseEntry? removed))
                {
                    Interlocked.Add(ref _totalCachedBytes, -removed.Payload.Length);
                    removed.Dispose();
                }
            }
        }
    }

    public static bool TryExtractDirectiveSeconds(ReadOnlySpan<char> cacheControl, ReadOnlySpan<char> directivePrefix, out int seconds)
    {
        int idx = cacheControl.IndexOf(directivePrefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            seconds = 0;
            return false;
        }

        ReadOnlySpan<char> after = cacheControl[(idx + directivePrefix.Length)..];
        int len = 0;
        while (len < after.Length && char.IsAsciiDigit(after[len]))
        {
            len++;
        }

        return int.TryParse(after[..len], out seconds);
    }

    private static string BuildCacheKey(string siteId, string path, string queryString, string encoding) =>
        $"{siteId}|{path}|{queryString}|{encoding}";

    public void Dispose()
    {
        foreach (var kvp in _entries)
        {
            kvp.Value.Dispose();
        }
        _entries.Clear();
    }
}

public sealed class CachedResponseEntry(
    int statusCode,
    IReadOnlyList<KeyValuePair<string, string>> headers,
    string etag,
    DateTimeOffset storedAtUtc,
    DateTimeOffset expiresAtUtc,
    PooledBytePayload payload) : IDisposable
{
    public int StatusCode { get; } = statusCode;
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; } = headers;
    public string ETag { get; } = etag;
    public DateTimeOffset StoredAtUtc { get; } = storedAtUtc;
    public DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;
    public PooledBytePayload Payload { get; } = payload;

    public void Dispose() => Payload.Dispose();
}
