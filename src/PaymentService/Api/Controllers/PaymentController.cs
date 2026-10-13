using Microsoft.AspNetCore.Mvc;
using PaymentService.Core.Dependencies;
using PaymentService.Domain.Schemas;
using PaymentService.Infrastructure.Services;

namespace PaymentService.Api.Controllers;

[ApiController]
public sealed class PaymentController(ReceiptService service) : ControllerBase
{
    [HttpGet("api/receipt/{receipt_id}")]
    public async Task<BaseResponse> GetReceipt([FromRoute(Name = "receipt_id")] Guid receiptId)
    {
        var result = await service.Get(receiptId, ClientIp.Of(HttpContext));
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        return result;
    }
}
