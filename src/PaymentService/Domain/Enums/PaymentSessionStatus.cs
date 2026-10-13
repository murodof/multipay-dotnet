namespace PaymentService.Domain.Enums;

public enum PaymentSessionStatus
{
    CREATED,
    OTP_SENDING,
    OTP_SENT,
    OTP_VERIFYING,
    CARD_VERIFIED,
    HELD,
    CONFIRMING,
    CANCELLING,
    CANCELLED,
    RETURNING,
    RETURNED,
    PAYING,
    PENDING_CHECK,
    PAID,
    CREDITING,
    REVERSING,
    REVERSED,
    SUCCESS,
    NEEDS_MANUAL,
    FAILED,
    EXPIRED,
}
