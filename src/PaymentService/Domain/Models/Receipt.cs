using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using PaymentService.Core.Configurations;

namespace PaymentService.Domain.Models;

public class Receipt : Base
{
    public Guid PaymentId { get; set; }

    [MaxLength(64)]
    public string Number { get; set; } = "";

    public JsonObject Data { get; set; } = [];
}
