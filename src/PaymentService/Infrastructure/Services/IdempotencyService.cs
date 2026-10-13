using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PaymentService.Core.Configurations;
using PaymentService.Domain.Schemas;
using PaymentService.Infrastructure.ExternalServices;
using PaymentService.Infrastructure.Utils;
using StatusCodes = PaymentService.Domain.Enums.StatusCodes;

namespace PaymentService.Infrastructure.Services;

public sealed record IdempotencyClaim(string Key, string Value, string BodyHash);

public sealed record IdempotencyStart(IdempotencyClaim? CacheKey, BaseResponse? Response);

public sealed class IdempotencyClaims(RedisClient redisClient)
{
    private readonly List<IdempotencyClaim> open = [];

    public void Add(IdempotencyClaim claim)
    {
        lock (open)
        {
            open.Add(claim);
        }
    }

    public void Remove(IdempotencyClaim claim)
    {
        lock (open)
        {
            open.RemoveAll(item => ReferenceEquals(item, claim));
        }
    }

    public async Task AbortOpenClaims()
    {
        IdempotencyClaim[] claims;
        lock (open)
        {
            claims = [.. open];
            open.Clear();
        }
        foreach (var claim in claims)
        {
            await redisClient.DeleteIfValue(claim.Key, claim.Value);
        }
    }
}

public sealed partial class IdempotencyService(RedisClient redisClient, IdempotencyClaims claims, IOptions<Settings> options)
{
    private Settings settings => options.Value;

    public async Task<IdempotencyStart> Start(string scope, string? key, IReadOnlyDictionary<string, object?>? body = null)
    {
        if (key is null || !KeyPattern().IsMatch(key))
        {
            return new IdempotencyStart(null, new ErrorResponse(StatusCodes.IDEMPOTENCY_REQUIRED));
        }
        var cacheKey = $"idem:{scope}:{key.ToLowerInvariant()}";
        var payload = new SortedDictionary<string, object?>(
            (IDictionary<string, object?>)(body ?? new Dictionary<string, object?>()).ToDictionary(),
            StringComparer.Ordinal);
        var bodyJson = JsonSerializer.Serialize(payload, WireJson.Options);
        var bodyHash = Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(settings.SECRET_KEYS), Encoding.UTF8.GetBytes(bodyJson)));
        var claimValue = new JsonObject
        {
            ["state"] = "IN_PROGRESS",
            ["body_hash"] = bodyHash,
            ["owner"] = Defaults.TokenHex(16),
        }.ToJsonString();
        if (await redisClient.SetValueNx(cacheKey, claimValue, settings.IDEMPOTENCY_TTL_SECONDS))
        {
            var claim = new IdempotencyClaim(cacheKey, claimValue, bodyHash);
            claims.Add(claim);
            return new IdempotencyStart(claim, null);
        }
        var cached = await redisClient.Get(cacheKey);
        if (cached is null)
        {
            return new IdempotencyStart(null, new ErrorResponse(StatusCodes.IN_PROGRESS));
        }
        try
        {
            var data = JsonNode.Parse(cached)!.AsObject();
            if (data["body_hash"]?.GetValue<string>() != bodyHash)
            {
                return new IdempotencyStart(null, new ErrorResponse(StatusCodes.IDEMPOTENCY_CONFLICT));
            }
            if (data["state"]?.GetValue<string>() == "IN_PROGRESS")
            {
                return new IdempotencyStart(null, new ErrorResponse(StatusCodes.IN_PROGRESS));
            }
            var response = data["response"]!.AsObject();
            if (response["success"]?.GetValue<bool>() == true)
            {
                return new IdempotencyStart(null, new SuccessResponse(response["result"]?.DeepClone()));
            }
            var error = response["error"]!.AsObject();
            var code = StatusCodes.ByName(error["code"]!.GetValue<string>());
            if (code is null)
            {
                return new IdempotencyStart(null, new ErrorResponse(StatusCodes.IN_PROGRESS));
            }
            var message = error["message"]?.AsObject().ToDictionary(item => item.Key, item => item.Value!.GetValue<string>());
            var details = error["details"]?.AsObject().ToDictionary(item => item.Key, item => (object?)item.Value?.DeepClone());
            return new IdempotencyStart(null, new ErrorResponse(code, message, details));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or NullReferenceException or FormatException)
        {
            return new IdempotencyStart(null, new ErrorResponse(StatusCodes.IN_PROGRESS));
        }
    }

    public async Task Complete(IdempotencyClaim? claim, BaseResponse response)
    {
        if (claim is not null)
        {
            var cached = new JsonObject
            {
                ["state"] = "DONE",
                ["body_hash"] = claim.BodyHash,
                ["response"] = response.ModelDump(),
            }.ToJsonString(WireJson.Options);
            await redisClient.SetIfValue(claim.Key, claim.Value, cached, settings.IDEMPOTENCY_TTL_SECONDS);
            claims.Remove(claim);
        }
    }

    [GeneratedRegex(@"\A[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}\z")]
    private static partial Regex KeyPattern();
}
