using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using PaymentService.Core.Configurations;

namespace PaymentService.Infrastructure.ExternalServices.Humo;

public sealed record HumoPaymentResult(bool Ok, JsonObject Result, bool TransportError = false) : IHumoResult
{
    public JsonNode? ErrorCode => null;
}

public class HumoPaymentClient(HttpClient http, IOptions<Settings> options)
{
    public const string SOAP_NS = "http://schemas.xmlsoap.org/soap/envelope/";
    public const string PAYMENT_NS = "urn:PaymentServer";

    private static readonly HashSet<string> Allowed =
    [
        "paymentID", "paymentRef", "status", "action", "auth_action_code",
        "auth_action_code_final", "auth_ref_number", "auth_appr_code", "auth_stan",
        "amount", "amount1a", "cardholder_amount1a", "internal_pan_masked",
    ];

    private Settings settings => options.Value;

    public static HumoPaymentResult ParsePaymentResponse(byte[] payload, string operation)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(payload), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return new HumoPaymentResult(false, []);
        }
        XNamespace soap = SOAP_NS;
        XNamespace payment = PAYMENT_NS;
        var body = document.Root?.Element(soap + "Body");
        if (body is null || body.Element(soap + "Fault") is not null)
        {
            return new HumoPaymentResult(false, []);
        }
        var response = body.Element(payment + $"{operation}Response");
        if (response is null)
        {
            return new HumoPaymentResult(false, []);
        }
        var result = new JsonObject();
        foreach (var element in response.DescendantsAndSelf())
        {
            var key = element.Name.LocalName;
            var text = Text(element);
            if (Allowed.Contains(key) && text is not null)
            {
                result[key] = text.Trim();
            }
            else if (key == "item")
            {
                var children = new Dictionary<string, string?>();
                foreach (var child in element.Elements())
                {
                    children[child.Name.LocalName] = Text(child);
                }
                var name = children.GetValueOrDefault("name");
                var value = children.GetValueOrDefault("value");
                if (name is not null && Allowed.Contains(name) && value is not null)
                {
                    result[name] = value.Trim();
                }
            }
        }
        return new HumoPaymentResult(true, result);
    }

    public static byte[] Envelope(
        string operation,
        IReadOnlyList<(string Key, string Value)> fields,
        IReadOnlyList<(string Key, string Value)>? details = null,
        bool directItems = false)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version='1.0' encoding='utf-8'?>\n");
        builder.Append($"<ns0:Envelope xmlns:ns0=\"{SOAP_NS}\" xmlns:ns1=\"{PAYMENT_NS}\"><ns0:Header /><ns0:Body><ns1:{operation}>");
        foreach (var (key, value) in fields.Where(field => field.Key != "paymentOriginator"))
        {
            Element(builder, key, value);
        }
        if (details is not null)
        {
            if (!directItems)
            {
                builder.Append(details.Count == 0 ? "<details />" : "<details>");
            }
            foreach (var (key, value) in details)
            {
                builder.Append("<item>");
                Element(builder, "name", key);
                Element(builder, "value", value);
                builder.Append("</item>");
            }
            if (!directItems && details.Count > 0)
            {
                builder.Append("</details>");
            }
        }
        foreach (var (key, value) in fields.Where(field => field.Key == "paymentOriginator"))
        {
            Element(builder, key, value);
        }
        builder.Append($"</ns1:{operation}></ns0:Body></ns0:Envelope>");
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public virtual async Task<HumoPaymentResult> Call(
        string operation,
        IReadOnlyList<(string Key, string Value)> fields,
        IReadOnlyList<(string Key, string Value)>? details = null,
        bool directItems = false)
    {
        if (settings.HUMO_PS_URL.Length == 0 || settings.HUMO_PS_USERNAME.Length == 0 || settings.HUMO_PS_PASSWORD.Length == 0)
        {
            return new HumoPaymentResult(false, []);
        }
        var payload = Envelope(operation, fields, details, directItems);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, settings.HUMO_PS_URL) { Content = new ByteArrayContent(payload) };
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/xml; charset=utf-8");
            request.Headers.ConnectionClose = true;
            request.Headers.TryAddWithoutValidation("SOAPAction", "\"\"");
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.HUMO_PS_USERNAME}:{settings.HUMO_PS_PASSWORD}")));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            using var response = await http.SendAsync(request, timeout.Token);
            var parsed = ParsePaymentResponse(await response.Content.ReadAsByteArrayAsync(timeout.Token), operation);
            if ((int)response.StatusCode >= 400 && !parsed.Ok)
            {
                return new HumoPaymentResult(false, [], true);
            }
            return parsed;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            return new HumoPaymentResult(false, [], true);
        }
    }

    public virtual Task<HumoPaymentResult> Hold(string paymentRef, string pan, string expiry, long amount) => Call(
        "Payment",
        [
            ("language", "en"), ("billerRef", "SOAP_DMS"), ("payinstrRef", "SOAP_DMS"),
            ("sessionID", paymentRef), ("paymentRef", paymentRef), ("paymentOriginator", settings.HUMO_PS_USERNAME),
        ],
        [
            ("pan", pan), ("expiry", expiry), ("ccy_code", "860"), ("amount", amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("merchant_id", settings.HUMO_MERCHANT_ID), ("terminal_id", settings.HUMO_TERMINAL_ID), ("centre_id", settings.HUMO_CENTRE_ID),
        ]);

    public virtual Task<HumoPaymentResult> Confirm(string paymentRef) => Call(
        "Payment",
        [("paymentRef", paymentRef), ("confirmed", "true"), ("finished", "true"), ("paymentOriginator", settings.HUMO_PS_USERNAME)]);

    public virtual Task<HumoPaymentResult> GetPayment(string paymentRef) =>
        Call("GetPayment", [("paymentRef", paymentRef), ("paymentOriginator", settings.HUMO_PS_USERNAME)]);

    public virtual Task<HumoPaymentResult> Cancel(string paymentRef) =>
        Call("CancelRequest", [("paymentRef", paymentRef), ("paymentOriginator", settings.HUMO_PS_USERNAME)]);

    public virtual Task<HumoPaymentResult> ReturnPayment(string paymentRef, string paymentId) => Call(
        "ReturnPayment",
        [("paymentID", paymentId), ("paymentRef", paymentRef), ("paymentOriginator", settings.HUMO_PS_USERNAME)],
        [("merchant_id", settings.HUMO_MERCHANT_ID), ("terminal_id", settings.HUMO_TERMINAL_ID), ("centre_id", settings.HUMO_CENTRE_ID)],
        directItems: true);

    public virtual async Task<HumoPaymentResult> Reconcile(string reconciliationRef)
    {
        var authorized = await Call(
            "Payment",
            [("billerRef", "SOAP_RECO"), ("payinstrRef", "SOAP_RECO"), ("paymentRef", reconciliationRef), ("paymentOriginator", settings.HUMO_PS_USERNAME)],
            [("terminal_id", settings.HUMO_TERMINAL_ID)]);
        if (!authorized.Ok || authorized.Result["action"]?.GetValue<string>() != "4")
        {
            return authorized;
        }
        return await Call(
            "Payment",
            [
                ("payinstrRef", "SOAP_RECO"), ("paymentRef", reconciliationRef),
                ("confirmed", "true"), ("finished", "true"), ("paymentOriginator", settings.HUMO_PS_USERNAME),
            ]);
    }

    private static void Element(StringBuilder builder, string name, string value)
    {
        if (value.Length == 0)
        {
            builder.Append($"<{name} />");
            return;
        }
        builder.Append($"<{name}>")
            .Append(value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal))
            .Append($"</{name}>");
    }

    private static string? Text(XElement element)
    {
        var texts = element.Nodes().TakeWhile(node => node is not XElement).OfType<XText>().ToList();
        return texts.Count == 0 ? null : string.Concat(texts.Select(text => text.Value));
    }
}
