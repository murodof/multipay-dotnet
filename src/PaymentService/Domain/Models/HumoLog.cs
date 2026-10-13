using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using PaymentService.Core.Configurations;

namespace PaymentService.Domain.Models;

public class HumoLog : Base
{
    public Guid SessionId { get; set; }

    [MaxLength(8)]
    public string System { get; set; } = "";

    [MaxLength(64)]
    public string Method { get; set; } = "";

    public JsonNode RequestMasked { get; set; } = new JsonObject();

    public JsonNode ResponseMasked { get; set; } = new JsonObject();

    public int DurationMs { get; set; }

    [MaxLength(32)]
    public string? ErrorCode { get; set; }
}
