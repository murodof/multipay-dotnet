using System.Text.RegularExpressions;

namespace PaymentService.Infrastructure.Utils;

public static class Phone
{
    public static bool IsValidPhone(string phone) => Regex.IsMatch(phone, @"\A998[0-9]{9}\z");

    public static string MaskPhone(string phone) => $"+{phone[..3]} {phone[3..5]} *** ** {phone[^2..]}";
}
