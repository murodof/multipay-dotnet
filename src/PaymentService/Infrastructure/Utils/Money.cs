namespace PaymentService.Infrastructure.Utils;

public static class Money
{
    public static long CommissionFor(long amount, decimal commissionPercent) =>
        (long)Math.Round(amount * commissionPercent / 100, 0, MidpointRounding.AwayFromZero);
}
