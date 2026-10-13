using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PaymentService.Core.Configurations;
using PaymentService.Domain.Schemas;

namespace PaymentService.Infrastructure.ExternalServices.Uzcard;

public sealed record SvgateResult(
    bool Ok,
    JsonObject Result,
    JsonNode? ErrorCode,
    string? ErrorMessage,
    JsonNode RequestMasked,
    JsonNode ResponseMasked,
    int DurationMs,
    bool TransportError = false);

public class SvgateClient(HttpClient http, IOptions<Settings> options)
{
    private static readonly HashSet<string> Sensitive =
        ["id", "pan", "pan2", "expiry", "code", "token", "tokens", "cardid", "authorization"];

    private Settings settings => options.Value;

    public static JsonNode? Masked(JsonNode? value, string key = "")
    {
        if (Sensitive.Contains(key.ToLowerInvariant()))
        {
            return "***";
        }
        return value switch
        {
            JsonObject items => new JsonObject(items.Select(item => KeyValuePair.Create(item.Key, Masked(item.Value, item.Key)))),
            JsonArray items => new JsonArray([.. items.Select(item => Masked(item))]),
            _ => value?.DeepClone(),
        };
    }

    public static JsonObject? NormalizedResult(string method, JsonNode? result)
    {
        if (result is JsonObject item)
        {
            return item;
        }
        if (method is "trans.ext" or "trans.reverse" or "cards.remove.token" && result is JsonArray rows && rows.All(row => row is JsonObject))
        {
            return new JsonObject { [method == "cards.remove.token" ? "tokens" : "transactions"] = rows.DeepClone() };
        }
        return null;
    }

    public virtual async Task<SvgateResult> Call(string method, JsonObject parameters)
    {
        var requestData = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Guid.NewGuid().ToString(),
            ["method"] = method,
            ["params"] = parameters,
        };
        var started = Stopwatch.StartNew();
        JsonNode? responseData;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, settings.SVGATE_URL)
            {
                Content = new StringContent(requestData.ToJsonString(WireJson.Options), Encoding.UTF8, "application/json"),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.SVGATE_USERNAME}:{settings.SVGATE_PASSWORD}")));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(settings.SVGATE_TIMEOUT_SECONDS));
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTPStatusError {(int)response.StatusCode}", null, response.StatusCode);
            }
            responseData = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or ArgumentException)
        {
            return new SvgateResult(
                false,
                [],
                "TRANSPORT",
                exception.GetType().Name,
                Masked(requestData)!,
                new JsonObject { ["error"] = exception.GetType().Name },
                (int)started.ElapsedMilliseconds,
                true);
        }

        var duration = (int)started.ElapsedMilliseconds;
        var data = responseData as JsonObject;
        var error = data?["error"];
        var result = data is null ? new JsonObject() : data.TryGetPropertyValue("result", out var value) ? value : new JsonObject();
        var normalized = NormalizedResult(method, result);
        return new SvgateResult(
            error is null && normalized is not null,
            normalized ?? [],
            (error as JsonObject)?["code"]?.DeepClone(),
            (error as JsonObject)?["message"] is JsonValue message && message.GetValueKind() == JsonValueKind.String ? message.GetValue<string>() : null,
            Masked(requestData)!,
            Masked(responseData) ?? new JsonObject(),
            duration);
    }

    public virtual Task<SvgateResult> CardsNewOtp(string pan, string expiry) => Call("cards.new.otp", new JsonObject
    {
        ["card"] = new JsonObject
        {
            ["pan"] = pan,
            ["expiry"] = expiry,
            ["sms"] = new JsonObject
            {
                ["templateId"] = settings.SVGATE_TEMPLATE_ID,
                ["serviceName"] = settings.SVGATE_SERVICE_NAME.Length > 20 ? settings.SVGATE_SERVICE_NAME[..20] : settings.SVGATE_SERVICE_NAME,
            },
        },
    });

    public virtual Task<SvgateResult> CardsNewVerify(string otpId, string code)
    {
        JsonNode normalizedId = Utils.Py.Int(otpId) is { } number ? JsonValue.Create(number) : JsonValue.Create(otpId);
        return Call("cards.new.verify", new JsonObject { ["otp"] = new JsonObject { ["id"] = normalizedId, ["code"] = code } });
    }

    public virtual Task<SvgateResult> CardsRemoveToken(string token) =>
        Call("cards.remove.token", new JsonObject { ["tokens"] = new JsonArray(token) });

    public virtual Task<SvgateResult> TransPayPurpose(string paymentId, string phone, long amount, long commission, string cardId) =>
        Call("trans.pay.purpose", new JsonObject
        {
            ["tran"] = new JsonObject
            {
                ["purpose"] = "payment",
                ["receiverId"] = phone,
                ["amount"] = amount,
                ["cardId"] = cardId,
                ["commission"] = commission,
                ["currency"] = "860",
                ["ext"] = paymentId,
                ["merchantId"] = settings.SVGATE_MERCHANT_ID,
                ["terminalId"] = settings.SVGATE_TERMINAL_ID,
                ["merchantInfo"] = new JsonObject
                {
                    ["mcc"] = settings.SVGATE_MCC,
                    ["legalName"] = settings.SVGATE_LEGAL_NAME,
                    ["legalType"] = settings.SVGATE_LEGAL_TYPE,
                    ["legalId"] = settings.SVGATE_LEGAL_ID,
                    ["legalOKED"] = settings.SVGATE_LEGAL_OKED,
                },
            },
        });

    public virtual Task<SvgateResult> TransExt(string paymentId) => Call("trans.ext", new JsonObject { ["extId"] = paymentId });

    public virtual Task<SvgateResult> TransReverse(string refNum) => Call("trans.reverse", new JsonObject { ["tranId"] = refNum });
}
