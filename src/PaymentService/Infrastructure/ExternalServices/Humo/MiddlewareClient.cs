using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PaymentService.Core.Configurations;

namespace PaymentService.Infrastructure.ExternalServices.Humo;

public class HumoMiddlewareClient(HttpClient http, IOptions<Settings> options) : HumoRestClient(http, options, options.Value.HUMO_MW_URL)
{
    public virtual Task<HumoRestResult> Card2(string pan) =>
        Post("/v3/iiacs/card2", new JsonObject { ["primaryAccountNumber"] = pan });

    public virtual Task<HumoRestResult> Card(string pan) =>
        Post("/v3/iiacs/card", new JsonObject { ["primaryAccountNumber"] = pan, ["mb_flag"] = 0 });
}
