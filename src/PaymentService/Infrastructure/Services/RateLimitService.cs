using Microsoft.Extensions.Options;
using PaymentService.Core.Configurations;
using PaymentService.Infrastructure.ExternalServices;
using PaymentService.Infrastructure.Utils;

namespace PaymentService.Infrastructure.Services;

public sealed class RateLimitService(RedisClient redisClient, IOptions<Settings> options)
{
    private Settings settings => options.Value;

    public Task<long> IpWait(string ip, DateTimeOffset now) => Hit(
        $"ip:{ip}",
        now,
        [("1m", settings.RATE_LIMIT_IP_PER_MINUTE, 60), ("1h", settings.RATE_LIMIT_IP_PER_HOUR, 3600)]);

    public Task<long> PhoneWait(string phone, DateTimeOffset now) => Hit(
        $"phone:{Defaults.Sha256Hex(phone)[..32]}",
        now,
        [("10m", settings.RATE_LIMIT_PHONE_PER_10_MINUTES, 600)]);

    public async Task<long> CardWait(string panHash, string ip, DateTimeOffset now)
    {
        var waitMs = await redisClient.HitSlidingWindows(
            [($"rate:{{card}}:pan:{panHash}:1h", 5, 3600), ($"rate:{{card}}:ip:{ip}:10m", 10, 600)],
            now.ToUnixTimeMilliseconds());
        return Ceil(waitMs);
    }

    public Task<long> ReceiptWait(string ip, DateTimeOffset now) => Hit($"receipt-ip:{ip}", now, [("1m", 30, 60)]);

    public Task<long> StatusWait(string provider, string sessionId, string ip, DateTimeOffset now) =>
        Hit($"status:{provider}:{sessionId}:{ip}", now, [("1m", 30, 60)]);

    public async Task<long> Hit(string subject, DateTimeOffset now, IReadOnlyList<(string Name, int Limit, int WindowSeconds)> windows)
    {
        var waitMs = await redisClient.HitSlidingWindows(
            windows.Select(window => ($"rate:{{{subject}}}:{window.Name}", window.Limit, window.WindowSeconds)).ToList(),
            now.ToUnixTimeMilliseconds());
        return Ceil(waitMs);
    }

    private static long Ceil(long waitMs) => (waitMs + 999) / 1000;
}
