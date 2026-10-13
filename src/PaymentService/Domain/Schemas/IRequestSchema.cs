using System.Text.Json;

namespace PaymentService.Domain.Schemas;

public interface IRequestSchema<TSelf> where TSelf : class
{
    static abstract TSelf? Validate(JsonElement body);
}
