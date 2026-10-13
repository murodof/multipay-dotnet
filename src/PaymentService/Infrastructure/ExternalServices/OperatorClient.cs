using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PaymentService.Core.Configurations;
using PaymentService.Domain.Schemas;
using PaymentService.Infrastructure.Utils;

namespace PaymentService.Infrastructure.ExternalServices;

public sealed record OperatorResult(bool Ok, string? TransactionId, string? ErrorCode, bool Uncertain = false);

public class OperatorClient(HttpClient http, IOptions<Settings> options)
{
    private Settings settings => options.Value;

    public virtual async Task<OperatorResult> Credit(string orderId, string phone, long amount, string refNum)
    {
        JsonNode? data;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, settings.OPERATOR_URL)
            {
                Content = new StringContent(
                    new JsonObject { ["orderId"] = orderId, ["phone"] = phone, ["amount"] = amount, ["refNum"] = refNum }.ToJsonString(WireJson.Options),
                    Encoding.UTF8),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (settings.OPERATOR_TOKEN.Length > 0)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.OPERATOR_TOKEN);
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(settings.OPERATOR_TIMEOUT_SECONDS));
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new OperatorResult(false, null, "HTTPStatusError", true);
            }
            data = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or ArgumentException)
        {
            return new OperatorResult(false, null, exception.GetType().Name, true);
        }
        if (data is not JsonObject body)
        {
            return new OperatorResult(false, null, "InvalidResponse", true);
        }
        return new OperatorResult(
            Py.Truthy(body.Get("success")),
            Py.StrOrNone(body.Get("transactionId")),
            Py.StrOrNone(body.Get("errorCode")));
    }
}
