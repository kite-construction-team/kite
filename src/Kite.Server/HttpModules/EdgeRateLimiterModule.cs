using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.RateLimiting;
using Kite.Core.Models;
using Kite.Core.Routing;
using Kite.Server.State;
using Microsoft.AspNetCore.Http;

namespace Kite.Server.HttpModules;

/// <summary>
/// Zero-allocation edge rate limiter powered by <see cref="System.Threading.RateLimiting"/>.
/// Caches one <see cref="PartitionedRateLimiter{TResource}"/> per compiled config snapshot
/// and emits <c>RateLimit-Limit</c> / <c>Retry-After</c> HTTP 429 Problem Details on exhaustion.
/// </summary>
public static class EdgeRateLimiterModule
{
    private static readonly ConditionalWeakTable<CompiledSiteFeatures, PartitionedRateLimiter<string>> LimitersByFeature = new();

    public static ValueTask<(bool Allowed, int RetryAfterSeconds)> TryAcquireAsync(
        HttpContext context,
        CompiledSiteFeatures compiled,
        CancellationToken ct) =>
        TryAcquireAsync(context, compiled, siteId: null, distributedState: null, ct);

    public static async ValueTask<(bool Allowed, int RetryAfterSeconds)> TryAcquireAsync(
        HttpContext context,
        CompiledSiteFeatures compiled,
        string? siteId,
        IDistributedEdgeState? distributedState,
        CancellationToken ct)
    {
        RateLimitingConfig cfg = compiled.EffectiveConfig.RateLimiting;
        if (!cfg.Enabled)
        {
            return (true, 0);
        }

        string partitionKey = ResolvePartitionKey(context, cfg);
        int limit = Math.Max(1, cfg.PermitLimit);
        context.Response.Headers["RateLimit-Limit"] = limit.ToString(CultureInfo.InvariantCulture);

        if (distributedState is not null)
        {
            int windowSec = Math.Max(1, cfg.WindowSeconds);
            long epochBucket = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / windowSec;
            string redisKey = $"rl:{siteId ?? "default"}:{partitionKey}:{epochBucket}";
            try
            {
                long count = await distributedState.IncrementWindowAsync(redisKey, windowSec, ct).ConfigureAwait(false);
                if (count > 0)
                {
                    long remaining = Math.Max(0, limit - count);
                    context.Response.Headers["RateLimit-Remaining"] = remaining.ToString(CultureInfo.InvariantCulture);
                    if (count <= limit)
                    {
                        return (true, 0);
                    }

                    int retryAfterRedis = Math.Max(1, cfg.RetryAfterSeconds);
                    context.Response.Headers["Retry-After"] = retryAfterRedis.ToString(CultureInfo.InvariantCulture);
                    return (false, retryAfterRedis);
                }
            }
            catch
            {
                // Graceful fallback to local in-memory partitioned limiter when Redis is temporarily unreachable
            }
        }

        PartitionedRateLimiter<string> limiter = LimitersByFeature.GetValue(compiled, static c => BuildLimiter(c.EffectiveConfig.RateLimiting));

        using RateLimitLease lease = await limiter.AcquireAsync(partitionKey, 1, ct).ConfigureAwait(false);
        if (lease.IsAcquired)
        {
            return (true, 0);
        }

        int retryAfter = Math.Max(1, cfg.RetryAfterSeconds);
        if (lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retrySpan))
        {
            retryAfter = Math.Max(1, (int)Math.Ceiling(retrySpan.TotalSeconds));
        }

        string retryText = retryAfter.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers["Retry-After"] = retryText;
        context.Response.Headers["RateLimit-Remaining"] = "0";
        return (false, retryAfter);
    }

    private static string ResolvePartitionKey(HttpContext context, RateLimitingConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.PartitionByHeader) &&
            context.Request.Headers.TryGetValue(cfg.PartitionByHeader, out var headerVal) &&
            !string.IsNullOrWhiteSpace(headerVal))
        {
            return headerVal.ToString();
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "loopback";
    }

    private static PartitionedRateLimiter<string> BuildLimiter(RateLimitingConfig cfg)
    {
        int permitLimit = Math.Max(1, cfg.PermitLimit);
        int windowSeconds = Math.Max(1, cfg.WindowSeconds);
        int segments = Math.Clamp(cfg.SegmentsPerWindow, 1, 60);
        int queueLimit = Math.Max(0, cfg.QueueLimit);
        TimeSpan window = TimeSpan.FromSeconds(windowSeconds);

        return cfg.Algorithm switch
        {
            RateLimitAlgorithm.FixedWindow => PartitionedRateLimiter.Create<string, string>(key =>
                RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = window,
                    QueueLimit = queueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true
                })),

            RateLimitAlgorithm.TokenBucket => PartitionedRateLimiter.Create<string, string>(key =>
                RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = permitLimit,
                    TokensPerPeriod = permitLimit,
                    ReplenishmentPeriod = window,
                    QueueLimit = queueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true
                })),

            RateLimitAlgorithm.Concurrency => PartitionedRateLimiter.Create<string, string>(key =>
                RateLimitPartition.GetConcurrencyLimiter(key, _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = permitLimit,
                    QueueLimit = queueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                })),

            _ => PartitionedRateLimiter.Create<string, string>(key =>
                RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = window,
                    SegmentsPerWindow = segments,
                    QueueLimit = queueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true
                }))
        };
    }
}
