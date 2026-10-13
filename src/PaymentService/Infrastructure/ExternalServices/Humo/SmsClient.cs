using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PaymentService.Core.Configurations;

namespace PaymentService.Infrastructure.ExternalServices.Humo;

public class HumoSmsClient(HttpClient http, IOptions<Settings> options) : HumoRestClient(http, options, options.Value.HUMO_SMS_URL)
{
    public virtual Task<HumoRestResult> SendSms2(string pan, string expiry) =>
        Post("/v1/send/sms2", new JsonObject { ["pan"] = pan, ["expiry"] = expiry, ["serviceName"] = settings.HUMO_SERVICE_NAME });

    public virtual Task<HumoRestResult> VerifyCode2(string pan, string code) =>
        Post("/v1/send/code2", new JsonObject { ["pan"] = pan, ["code"] = code });

    public virtual Task<HumoRestResult> Deactivate(string pan) =>
        Post("/v1/permission/deactivate", new JsonObject { ["pan"] = pan });
}
