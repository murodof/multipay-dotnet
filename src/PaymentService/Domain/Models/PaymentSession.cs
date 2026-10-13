using System.ComponentModel.DataAnnotations;
using PaymentService.Core.Configurations;
using PaymentService.Domain.Enums;

namespace PaymentService.Domain.Models;

public class PaymentSession : Base
{
    [MaxLength(64)]
    public string SessionTokenHash { get; set; } = "";

    [MaxLength(64)]
    public string OrderId { get; set; } = "";

    [MaxLength(12)]
    public string Phone { get; set; } = "";

    public long Amount { get; set; }

    public long Commission { get; set; }

    public long Total { get; set; }

    [MaxLength(16)]
    public string? Processing { get; set; }

    [MaxLength(64)]
    public string? FailCode { get; set; }

    [MaxLength(64)]
    public string? Ip { get; set; }

    public string? UserAgent { get; set; }

    [MaxLength(32)]
    public string? PanMasked { get; set; }

    [MaxLength(64)]
    public string? PanHash { get; set; }

    [MaxLength(255)]
    public string? CardHolder { get; set; }

    [MaxLength(32)]
    public string? OtpId { get; set; }

    public DateTimeOffset? OtpSentAt { get; set; }

    public int OtpAttempts { get; set; }

    public int OtpResends { get; set; }

    public int CardAttempts { get; set; }

    [MaxLength(128)]
    public string? SvCardToken { get; set; }

    public DateTimeOffset? LastActivityAt { get; set; }

    public PaymentSessionStatus Status { get; set; } = PaymentSessionStatus.CREATED;

    public DateTimeOffset ExpiresAt { get; set; }
}
