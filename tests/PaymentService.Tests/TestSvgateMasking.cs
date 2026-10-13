using System.Text.Json.Nodes;
using PaymentService.Infrastructure.ExternalServices.Uzcard;

namespace PaymentService.Tests;

public sealed class TestSvgateMasking
{
    [Fact]
    public void Sensitive_svgate_fields_are_masked_recursively()
    {
        var source = JsonNode.Parse("""
            {"Authorization": "Basic secret",
             "params": {"card": {"pan": "8600123412341239", "expiry": "2912"}, "otp": {"code": "A12B34"}, "cardId": "token-value"}}
            """);

        var result = SvgateClient.Masked(source);

        Assert.Equal(
            """{"Authorization":"***","params":{"card":{"pan":"***","expiry":"***"},"otp":{"code":"***"},"cardId":"***"}}""",
            result!.ToJsonString());
    }
}
