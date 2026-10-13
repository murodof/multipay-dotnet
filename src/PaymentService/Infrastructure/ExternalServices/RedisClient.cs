using Microsoft.Extensions.Options;
using PaymentService.Core.Configurations;
using StackExchange.Redis;

namespace PaymentService.Infrastructure.ExternalServices;

public sealed class RedisClient : IAsyncDisposable
{
    private const string SlidingWindow = """
            local now = tonumber(ARGV[1])
            local member = ARGV[2]
            local wait = 0
            for i, key in ipairs(KEYS) do
              local window = tonumber(ARGV[2 * i + 1])
              local limit = tonumber(ARGV[2 * i + 2])
              redis.call('ZREMRANGEBYSCORE', key, '-inf', now - window)
              if redis.call('ZCARD', key) >= limit then
                local oldest = redis.call('ZRANGE', key, 0, 0, 'WITHSCORES')
                local left = window - (now - tonumber(oldest[2]))
                if left > wait then wait = left end
              end
            end
            if wait > 0 then return wait end
            for i, key in ipairs(KEYS) do
              redis.call('ZADD', key, now, member)
              redis.call('PEXPIRE', key, tonumber(ARGV[2 * i + 1]))
            end
            return 0
            """;

    private const string CompareSet = """
            if redis.call('GET', KEYS[1]) ~= ARGV[1] then return 0 end
            redis.call('SET', KEYS[1], ARGV[2], 'EX', tonumber(ARGV[3]))
            return 1
            """;

    private const string CompareDelete = """
            if redis.call('GET', KEYS[1]) ~= ARGV[1] then return 0 end
            return redis.call('DEL', KEYS[1])
            """;

    private readonly Lazy<Task<ConnectionMultiplexer>> connection;
    private readonly int database;

    public RedisClient(IOptions<Settings> settings)
    {
        var url = RedisUrl.Parse(settings.Value.REDIS_URL) ?? new RedisUrl("localhost", 6379, 0, null, null, false);
        database = url.Database;
        var options = new ConfigurationOptions
        {
            EndPoints = { { url.Host, url.Port } },
            User = url.User,
            Password = url.Password,
            Ssl = url.Ssl,
            AbortOnConnectFail = false,
        };
        connection = new Lazy<Task<ConnectionMultiplexer>>(() => ConnectionMultiplexer.ConnectAsync(options));
    }

    public async Task<IDatabase> Redis() => (await connection.Value).GetDatabase(database);

    public async Task<bool> SetNx(string key, int ttlSeconds) =>
        await (await Redis()).StringSetAsync(key, 1, TimeSpan.FromSeconds(ttlSeconds), When.NotExists);

    public async Task<bool> SetValue(string key, string value, int ttlSeconds) =>
        await (await Redis()).StringSetAsync(key, value, TimeSpan.FromSeconds(ttlSeconds));

    public async Task<bool> SetValueNx(string key, string value, int ttlSeconds) =>
        await (await Redis()).StringSetAsync(key, value, TimeSpan.FromSeconds(ttlSeconds), When.NotExists);

    public async Task<string?> Get(string key) => await (await Redis()).StringGetAsync(key);

    public async Task<long> Delete(string key) => await (await Redis()).KeyDeleteAsync(key) ? 1 : 0;

    public async Task<long> Ttl(string key) => (long)await (await Redis()).ExecuteAsync("TTL", key);

    public async Task<long> HitSlidingWindows(IReadOnlyList<(string Key, int Limit, int WindowSeconds)> windows, long nowMs)
    {
        var args = new List<RedisValue> { nowMs, $"{nowMs}-{Guid.NewGuid():N}" };
        foreach (var window in windows)
        {
            args.Add(window.WindowSeconds * 1000L);
            args.Add(window.Limit);
        }
        var keys = windows.Select(window => (RedisKey)window.Key).ToArray();
        return (long)await (await Redis()).ScriptEvaluateAsync(SlidingWindow, keys, [.. args]);
    }

    public async Task<bool> SetIfValue(string key, string old, string value, int ttlSeconds) =>
        (long)await (await Redis()).ScriptEvaluateAsync(CompareSet, [key], [old, value, ttlSeconds]) == 1;

    public async Task<bool> DeleteIfValue(string key, string value) =>
        (long)await (await Redis()).ScriptEvaluateAsync(CompareDelete, [key], [value]) != 0;

    public async ValueTask DisposeAsync()
    {
        if (connection.IsValueCreated)
        {
            await (await connection.Value).DisposeAsync();
        }
    }
}
