namespace PaymentService.Domain.Enums;

public enum PaymentStatus
{
    PAYING,
    HELD,
    CONFIRMING,
    CANCELLING,
    CANCELLED,
    RETURNING,
    RETURNED,
    PENDING_CHECK,
    PAID,
    CREDITING,
    SUCCESS,
    FAILED,
    REVERSING,
    REVERSED,
    NEEDS_MANUAL,
}
