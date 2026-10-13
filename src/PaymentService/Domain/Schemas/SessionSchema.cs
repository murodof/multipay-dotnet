using System.Text.Json;

namespace PaymentService.Domain.Schemas;

public sealed record SessionCreateSchema(string? Hash) : IRequestSchema<SessionCreateSchema>
{
    public static SessionCreateSchema? Validate(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (!body.TryGetProperty("hash", out var hash) || hash.ValueKind == JsonValueKind.Null)
        {
            return new SessionCreateSchema((string?)null);
        }
        return hash.ValueKind == JsonValueKind.String ? new SessionCreateSchema(hash.GetString()) : null;
    }
}

public sealed record SessionCreatedResponse(
    Guid SessionId,
    string SessionToken,
    string PhoneMask,
    long Amount,
    long Commission,
    long Total,
    DateTimeOffset ExpiresAt,
    int IdleTimeout);
