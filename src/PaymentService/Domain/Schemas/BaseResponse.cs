using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using StatusCodes = PaymentService.Domain.Enums.StatusCodes;

namespace PaymentService.Domain.Schemas;

public abstract class BaseResponse : IActionResult
{
    public abstract bool Success { get; }

    public virtual int HttpStatus => 200;

    public abstract JsonObject ModelDump();

    public byte[] ToJsonBytes() => JsonSerializer.SerializeToUtf8Bytes(ModelDump(), WireJson.Options);

    public virtual void ApplyTo(HttpResponse response) => response.StatusCode = HttpStatus;

    public async Task ExecuteResultAsync(ActionContext context)
    {
        var response = context.HttpContext.Response;
        ApplyTo(response);
        var body = ToJsonBytes();
        response.ContentType = "application/json";
        response.ContentLength = body.Length;
        await response.Body.WriteAsync(body, context.HttpContext.RequestAborted);
    }
}

public sealed class SuccessResponse(object? result) : BaseResponse
{
    public override bool Success => true;

    public object? Result { get; } = result;

    public JsonNode? ResultNode => JsonSerializer.SerializeToNode(Result, WireJson.Options);

    public override JsonObject ModelDump() => new()
    {
        ["success"] = true,
        ["result"] = ResultNode,
    };
}

public sealed class ErrorResponse(
    StatusCodes statusCode,
    IReadOnlyDictionary<string, string>? message = null,
    IReadOnlyDictionary<string, object?>? details = null) : BaseResponse
{
    public override bool Success => false;

    public StatusCodes StatusCode { get; } = statusCode;

    public string Code => StatusCode.Name;

    public IReadOnlyDictionary<string, string> Message { get; } = message ?? statusCode.Message;

    public IReadOnlyDictionary<string, object?>? Details { get; } = details;

    public override int HttpStatus => StatusCode.HttpStatus;

    public JsonObject Error
    {
        get
        {
            var error = new JsonObject
            {
                ["code"] = StatusCode.Name,
                ["error_code"] = StatusCode.Value,
                ["message"] = JsonSerializer.SerializeToNode(Message, WireJson.Options),
            };
            if (Details is not null)
            {
                error["details"] = JsonSerializer.SerializeToNode(Details, WireJson.Options);
            }
            return error;
        }
    }

    public override JsonObject ModelDump() => new()
    {
        ["success"] = false,
        ["error"] = Error,
    };

    public override void ApplyTo(HttpResponse response)
    {
        response.StatusCode = HttpStatus;
        if (Details is not null && Details.TryGetValue("retry_after", out var retryAfter) && retryAfter is not null)
        {
            response.Headers.RetryAfter = WireJson.Text(retryAfter);
        }
    }
}

public static class WireJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        Encoder = PythonJsonEncoder.Instance,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new PydanticDateTimeConverter() },
    };

    public static string Text(object value) => value switch
    {
        string text => text,
        JsonValue node when node.GetValueKind() == JsonValueKind.String => node.GetValue<string>(),
        JsonNode node => node.ToJsonString(Options),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    public static string Format(DateTimeOffset value)
    {
        var text = value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var microseconds = value.Ticks % TimeSpan.TicksPerSecond / 10;
        if (microseconds != 0)
        {
            text += "." + microseconds.ToString("D6", CultureInfo.InvariantCulture);
        }
        return text + (value.Offset == TimeSpan.Zero ? "Z" : value.ToString("zzz", CultureInfo.InvariantCulture));
    }
}

public sealed class PydanticDateTimeConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(WireJson.Format(value));
}

public sealed class PythonJsonEncoder : JavaScriptEncoder
{
    public static readonly PythonJsonEncoder Instance = new();

    public override int MaxOutputCharactersPerInputCharacter => 6;

    public override bool WillEncode(int unicodeScalar) => unicodeScalar < 0x20 || unicodeScalar == '"' || unicodeScalar == '\\';

    public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
    {
        for (var index = 0; index < textLength; index++)
        {
            if (WillEncode(text[index]))
            {
                return index;
            }
        }
        return -1;
    }

    public override unsafe bool TryEncodeUnicodeScalar(int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten)
    {
        var escaped = unicodeScalar switch
        {
            '"' => "\\\"",
            '\\' => "\\\\",
            '\n' => "\\n",
            '\r' => "\\r",
            '\t' => "\\t",
            '\b' => "\\b",
            '\f' => "\\f",
            < 0x20 => "\\u" + unicodeScalar.ToString("x4", CultureInfo.InvariantCulture),
            _ => char.ConvertFromUtf32(unicodeScalar),
        };
        if (escaped.Length > bufferLength)
        {
            numberOfCharactersWritten = 0;
            return false;
        }
        for (var index = 0; index < escaped.Length; index++)
        {
            buffer[index] = escaped[index];
        }
        numberOfCharactersWritten = escaped.Length;
        return true;
    }
}
