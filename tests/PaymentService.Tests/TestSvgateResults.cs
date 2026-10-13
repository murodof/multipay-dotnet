using System.Text.Json.Nodes;
using PaymentService.Infrastructure.ExternalServices.Uzcard;
using PaymentService.Infrastructure.Services;

namespace PaymentService.Tests;

public sealed class TestSvgateResults
{
    [Fact]
    public void Transaction_results_keep_debit_and_reversal_rows()
    {
        var rows = JsonNode.Parse("""
            [{"tranType": "DEBIT", "status": "OK", "resp": 0, "refNum": "123456789012"},
             {"tranType": "REVERSAL", "status": "ROK", "resp": 0, "refNum": "123456789012"}]
            """);

        var result = SvgateClient.NormalizedResult("trans.ext", rows)!;

        Assert.Equal(new JsonObject { ["transactions"] = rows!.DeepClone() }.ToJsonString(), result.ToJsonString());
        Assert.True(UzcardService.SuccessfulReversal(result, "123456789012"));
        Assert.False(UzcardService.SuccessfulReversal(result, "another-reference"));
    }

    [Fact]
    public void Empty_transaction_list_cannot_confirm_reverse()
    {
        var result = SvgateClient.NormalizedResult("trans.reverse", new JsonArray())!;

        Assert.Equal("""{"transactions":[]}""", result.ToJsonString());
        Assert.False(UzcardService.SuccessfulReversal(result, "123456789012"));
    }

    [Fact]
    public void Remove_token_result_and_audit_masking()
    {
        const string token = "card-token-secret";

        Assert.Equal(
            """{"tokens":[{"id":"card-token-secret","Status":1}]}""",
            SvgateClient.NormalizedResult("cards.remove.token", JsonNode.Parse("""[{"id": "card-token-secret", "Status": 1}]"""))!.ToJsonString());
        Assert.Equal(
            """{"params":{"tokens":"***"},"result":[{"id":"***","pan2":"***"}]}""",
            SvgateClient.Masked(new JsonObject
            {
                ["params"] = new JsonObject { ["tokens"] = new JsonArray(token) },
                ["result"] = new JsonArray(new JsonObject { ["id"] = token, ["pan2"] = "8600123412341239" }),
            })!.ToJsonString());
    }

    [Fact]
    public void Non_object_results_are_not_normalised_for_other_methods() =>
        Assert.Null(SvgateClient.NormalizedResult("cards.new.otp", new JsonArray()));
}
