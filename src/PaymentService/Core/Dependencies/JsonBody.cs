using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using PaymentService.Domain.Schemas;

namespace PaymentService.Core.Dependencies;

public sealed class JsonBodyBinder<T> : IModelBinder where T : class, IRequestSchema<T>
{
    public const string UndecodableBody = "__undecodable_body__";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var request = bindingContext.HttpContext.Request;
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, bindingContext.HttpContext.RequestAborted);
        var bytes = buffer.ToArray();
        if (bytes.Length == 0 || !IsJson(request.ContentType))
        {
            bindingContext.ModelState.AddModelError(bindingContext.ModelName, "body");
            return;
        }
        string text;
        try
        {
            var start = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0;
            text = StrictUtf8.GetString(bytes, start, bytes.Length - start);
        }
        catch (DecoderFallbackException)
        {
            bindingContext.ModelState.AddModelError(UndecodableBody, "body");
            return;
        }
        T? model;
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 1000 });
            model = T.Validate(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            model = null;
        }
        if (model is null)
        {
            bindingContext.ModelState.AddModelError(bindingContext.ModelName, "body");
            return;
        }
        bindingContext.Result = ModelBindingResult.Success(model);
    }

    private static bool IsJson(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType) || !MediaTypeHeaderValue.TryParse(contentType, out var parsed) || parsed.MediaType is null)
        {
            return false;
        }
        var parts = parsed.MediaType.ToLowerInvariant().Split('/', 2);
        return parts.Length == 2 && parts[0] == "application" && (parts[1] == "json" || parts[1].EndsWith("+json", StringComparison.Ordinal));
    }
}
