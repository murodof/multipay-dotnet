using System.ComponentModel.DataAnnotations;
using PaymentService.Core.Configurations;
using PaymentService.Domain.Enums;

namespace PaymentService.Domain.Models;

public class Payment : Base
{
    public Guid SessionId { get; set; }

    public long Amount { get; set; }

    public long Commission { get; set; }

    [MaxLength(3)]
    public string Currency { get; set; } = "860";

    [MaxLength(64)]
    public string? SvRefNum { get; set; }

    [MaxLength(32)]
    public string? SvStan { get; set; }

    [MaxLength(32)]
    public string? SvDate12 { get; set; }

    [MaxLength(16)]
    public string? SvResp { get; set; }

    [MaxLength(16)]
    public string? SvStatus { get; set; }

    [MaxLength(64)]
    public string? HumoPaymentId { get; set; }

    [MaxLength(64)]
    public string? HumoRrn { get; set; }

    [MaxLength(32)]
    public string? HumoApprCode { get; set; }

    [MaxLength(16)]
    public string? HumoAction { get; set; }

    public long? AmountCardholder { get; set; }

    [MaxLength(128)]
    public string? OperatorTxId { get; set; }

    public PaymentStatus Status { get; set; } = PaymentStatus.PAYING;

    public int CheckAttempts { get; set; }

    public DateTimeOffset? NextCheckAt { get; set; }
}
