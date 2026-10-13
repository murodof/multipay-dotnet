using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PaymentService.Core.Configurations;
using PaymentService.Domain.Schemas;
using PaymentService.Infrastructure.Utils;

namespace PaymentService.Infrastructure.ExternalServices.Humo;

public interface IHumoResult
{
    bool Ok { get; }

    JsonObject Result { get; }

    JsonNode? ErrorCode { get; }

    bool TransportError { get; }
}

public sealed record HumoRestResult(bool Ok, JsonObject Result, JsonNode? ErrorCode = null, bool TransportError = false) : IHumoResult;

public abstract class HumoRestClient(HttpClient http, IOptions<Settings> options, string baseUrl)
{
    private readonly string baseUrl = baseUrl.TrimEnd('/');

    protected Settings settings => options.Value;

    public async Task<HumoRestResult> Post(string path, JsonObject parameters)
    {
        if (baseUrl.Length == 0 || settings.HUMO_BEARER_TOKEN.Length == 0)
        {
            return new HumoRestResult(false, [], "CONFIG");
        }
        var requestId = Guid.NewGuid().ToString();
        JsonNode? data;
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}{path}")
            {
                Content = new StringContent(new JsonObject { ["id"] = requestId, ["params"] = parameters }.ToJsonString(WireJson.Options), Encoding.UTF8),
            };
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json; charset=utf-8");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.HUMO_BEARER_TOKEN);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            response = await http.SendAsync(request, timeout.Token);
            data = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or ArgumentException)
        {
            return new HumoRestResult(false, [], "TRANSPORT", true);
        }
        using (response)
        {
            if (data is not JsonObject body || Py.Str(body.Get("id")) != requestId)
            {
                return new HumoRestResult(false, [], "INVALID_RESPONSE");
            }
            var error = body.Get("error");
            var result = body.Get("result");
            if (error is not null)
            {
                return new HumoRestResult(false, [], error is JsonObject item ? item.Get("code")?.DeepClone() : "INVALID_RESPONSE");
            }
            if ((int)response.StatusCode >= 400)
            {
                return new HumoRestResult(false, [], (int)response.StatusCode);
            }
            if (result is not JsonObject resultObject)
            {
                return new HumoRestResult(false, [], "INVALID_RESPONSE");
            }
            return new HumoRestResult(true, resultObject.DeepClone().AsObject());
        }
    }
}
