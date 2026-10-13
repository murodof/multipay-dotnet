using System.Text.Json.Nodes;
using PaymentService.Domain.Schemas;
using PaymentService.Infrastructure.Repositories;
using PaymentService.Infrastructure.Utils;
using StatusCodes = PaymentService.Domain.Enums.StatusCodes;

namespace PaymentService.Infrastructure.Services;

public sealed class ReceiptService(ReceiptRepository receiptRepo, RateLimitService rateLimit, TimeProvider clock)
{
    public async Task<BaseResponse> Get(Guid receiptId, string clientIp)
    {
        var wait = await rateLimit.ReceiptWait(clientIp, clock.UtcNow());
        if (wait != 0)
        {
            return new ErrorResponse(StatusCodes.RATE_LIMITED, details: new Dictionary<string, object?> { ["retry_after"] = wait });
        }
        var receipt = await receiptRepo.GetById(receiptId);
        if (receipt is null)
        {
            return new ErrorResponse(StatusCodes.NOT_FOUND);
        }
        var result = new JsonObject { ["id"] = receipt.Id.ToString(), ["number"] = receipt.Number };
        foreach (var (key, value) in receipt.Data)
        {
            result[key] = value?.DeepClone();
        }
        return new SuccessResponse(result);
    }
}
