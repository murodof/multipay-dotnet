namespace PaymentService.Domain.Enums;

public sealed class StatusCodes
{
    public static readonly StatusCodes UNKNOWN_ERROR = new(nameof(UNKNOWN_ERROR), -2000, 500, "Noma'lum xatolik!", "Unknown error occurred", "Что-то пошло не так");
    public static readonly StatusCodes HASH_INVALID = new(nameof(HASH_INVALID), -2001, 401, "To'lov havolasi yaroqsiz!", "Payment link is invalid", "Ссылка на оплату недействительна");
    public static readonly StatusCodes HASH_EXPIRED = new(nameof(HASH_EXPIRED), -2002, 401, "To'lov havolasining muddati tugagan!", "Payment link has expired", "Срок действия ссылки на оплату истёк");
    public static readonly StatusCodes NONCE_USED = new(nameof(NONCE_USED), -2003, 409, "Bu to'lov havolasi allaqachon ishlatilgan!", "This payment link has already been used", "Эта ссылка на оплату уже использована");
    public static readonly StatusCodes ORDER_EXISTS = new(nameof(ORDER_EXISTS), -2004, 409, "Bu buyurtma uchun to'lov sessiyasi allaqachon mavjud!", "A payment session already exists for this order", "Платёжная сессия для этого заказа уже существует");
    public static readonly StatusCodes PHONE_INVALID = new(nameof(PHONE_INVALID), -2005, 422, "Telefon raqami noto'g'ri!", "Invalid phone number", "Неверный номер телефона");
    public static readonly StatusCodes AMOUNT_OUT_OF_RANGE = new(nameof(AMOUNT_OUT_OF_RANGE), -2006, 422, "To'lov summasi ruxsat etilgan oraliqda emas!", "Amount is outside the allowed range", "Сумма вне допустимого диапазона");
    public static readonly StatusCodes RATE_LIMITED = new(nameof(RATE_LIMITED), -2007, 429, "Juda ko'p urinish, biroz kutib turing!", "Too many attempts, please wait", "Слишком много попыток, подождите");
    public static readonly StatusCodes INVALID_REQUEST = new(nameof(INVALID_REQUEST), -2008, 422, "So'rov formati noto'g'ri!", "Malformed request", "Неверный формат запроса");
    public static readonly StatusCodes NOT_FOUND = new(nameof(NOT_FOUND), -2009, 404, "Manzil topilmadi!", "Not found", "Не найдено");
    public static readonly StatusCodes METHOD_NOT_ALLOWED = new(nameof(METHOD_NOT_ALLOWED), -2010, 405, "Bu metodga ruxsat yo'q!", "Method not allowed", "Метод не разрешён");
    public static readonly StatusCodes SESSION_UNAUTHORIZED = new(nameof(SESSION_UNAUTHORIZED), -2011, 401, "To'lov sessiyasiga ruxsat yo'q!", "Payment session is unauthorized", "Нет доступа к платёжной сессии");
    public static readonly StatusCodes SESSION_EXPIRED = new(nameof(SESSION_EXPIRED), -2012, 409, "To'lov sessiyasining muddati tugagan!", "Payment session has expired", "Срок платёжной сессии истёк");
    public static readonly StatusCodes INVALID_STATE = new(nameof(INVALID_STATE), -2013, 409, "Amalni sessiyaning joriy holatida bajarib bo'lmaydi!", "Operation is not allowed in the current session state", "Операция недоступна в текущем состоянии сессии");
    public static readonly StatusCodes IDEMPOTENCY_REQUIRED = new(nameof(IDEMPOTENCY_REQUIRED), -2014, 422, "Idempotency-Key sarlavhasi talab qilinadi!", "Idempotency-Key header is required", "Требуется заголовок Idempotency-Key");
    public static readonly StatusCodes IN_PROGRESS = new(nameof(IN_PROGRESS), -2015, 409, "So'rov bajarilmoqda!", "Request is in progress", "Запрос выполняется");
    public static readonly StatusCodes CARD_INVALID = new(nameof(CARD_INVALID), -2016, 422, "Karta raqami yoki amal qilish muddati noto'g'ri!", "Card number or expiry is invalid", "Неверный номер карты или срок действия");
    public static readonly StatusCodes CARD_NOT_SUPPORTED = new(nameof(CARD_NOT_SUPPORTED), -2017, 422, "Ushbu karta bilan to'lab bo'lmaydi!", "This card is not supported", "Эта карта не поддерживается");
    public static readonly StatusCodes CARD_BLOCKED = new(nameof(CARD_BLOCKED), -2018, 409, "Karta bloklangan. Bankingizga murojaat qiling!", "Card is blocked. Contact your bank", "Карта заблокирована. Обратитесь в банк");
    public static readonly StatusCodes CARD_TEMP_BLOCKED = new(nameof(CARD_TEMP_BLOCKED), -2019, 429, "Karta vaqtincha bloklangan!", "Card is temporarily blocked", "Карта временно заблокирована");
    public static readonly StatusCodes OTP_WRONG = new(nameof(OTP_WRONG), -2020, 422, "Kod noto'g'ri!", "The code is incorrect", "Неверный код");
    public static readonly StatusCodes OTP_EXPIRED = new(nameof(OTP_EXPIRED), -2021, 409, "Kod muddati tugadi!", "The code has expired", "Срок действия кода истёк");
    public static readonly StatusCodes OTP_LOCKED = new(nameof(OTP_LOCKED), -2022, 429, "Kod 3 marta noto'g'ri kiritildi. Karta vaqtincha bloklandi!", "The code was entered incorrectly 3 times. The card is temporarily blocked", "Код введён неверно 3 раза. Карта временно заблокирована");
    public static readonly StatusCodes OTP_RESEND_TOO_SOON = new(nameof(OTP_RESEND_TOO_SOON), -2023, 429, "Kodni qayta yuborish uchun kuting!", "Wait before resending the code", "Подождите перед повторной отправкой кода");
    public static readonly StatusCodes OTP_RESEND_LIMIT = new(nameof(OTP_RESEND_LIMIT), -2024, 429, "Kodni qayta yuborish limiti tugadi!", "OTP resend limit reached", "Лимит повторной отправки кода исчерпан");
    public static readonly StatusCodes INSUFFICIENT_FUNDS = new(nameof(INSUFFICIENT_FUNDS), -2025, 422, "Kartada mablag' yetarli emas!", "Insufficient card balance", "Недостаточно средств на карте");
    public static readonly StatusCodes LIMIT_EXCEEDED = new(nameof(LIMIT_EXCEEDED), -2026, 422, "Karta limiti oshib ketdi!", "Card limit exceeded", "Превышен лимит карты");
    public static readonly StatusCodes FRAUD_DECLINED = new(nameof(FRAUD_DECLINED), -2027, 403, "To'lov rad etildi!", "Payment was declined", "Платёж отклонён");
    public static readonly StatusCodes PROCESSING_UNAVAILABLE = new(nameof(PROCESSING_UNAVAILABLE), -2028, 503, "To'lov xizmati vaqtincha ishlamayapti!", "Payment service is temporarily unavailable", "Платёжный сервис временно недоступен");
    public static readonly StatusCodes PAYMENT_FAILED = new(nameof(PAYMENT_FAILED), -2029, 422, "To'lov amalga oshmadi!", "Payment failed", "Платёж не выполнен");
    public static readonly StatusCodes STRONG_AUTH_REQUIRED = new(nameof(STRONG_AUTH_REQUIRED), -2030, 503, "Qo'shimcha tasdiqlash xizmati sozlanmagan!", "Additional authentication service is not configured", "Сервис дополнительного подтверждения не настроен");
    public static readonly StatusCodes SMS_FAILED = new(nameof(SMS_FAILED), -2031, 503, "SMS yuborishda xatolik, qayta urinib ko'ring!", "Failed to send SMS, please try again", "Не удалось отправить SMS, попробуйте снова");
    public static readonly StatusCodes CORPORATE_CARD = new(nameof(CORPORATE_CARD), -2032, 422, "Korporativ karta bilan to'lab bo'lmaydi!", "Corporate cards are not supported", "Оплата корпоративной картой недоступна");
    public static readonly StatusCodes NO_PHONE = new(nameof(NO_PHONE), -2033, 422, "Bu kartaga telefon raqami bog'lanmagan!", "No phone number is linked to this card", "К этой карте не привязан номер телефона");
    public static readonly StatusCodes IDEMPOTENCY_CONFLICT = new(nameof(IDEMPOTENCY_CONFLICT), -2034, 409, "Bu Idempotency-Key boshqa so'rov uchun ishlatilgan!", "Idempotency-Key was used for a different request", "Idempotency-Key использован для другого запроса");

    public static readonly IReadOnlyList<StatusCodes> All =
    [
        UNKNOWN_ERROR,
        HASH_INVALID,
        HASH_EXPIRED,
        NONCE_USED,
        ORDER_EXISTS,
        PHONE_INVALID,
        AMOUNT_OUT_OF_RANGE,
        RATE_LIMITED,
        INVALID_REQUEST,
        NOT_FOUND,
        METHOD_NOT_ALLOWED,
        SESSION_UNAUTHORIZED,
        SESSION_EXPIRED,
        INVALID_STATE,
        IDEMPOTENCY_REQUIRED,
        IN_PROGRESS,
        CARD_INVALID,
        CARD_NOT_SUPPORTED,
        CARD_BLOCKED,
        CARD_TEMP_BLOCKED,
        OTP_WRONG,
        OTP_EXPIRED,
        OTP_LOCKED,
        OTP_RESEND_TOO_SOON,
        OTP_RESEND_LIMIT,
        INSUFFICIENT_FUNDS,
        LIMIT_EXCEEDED,
        FRAUD_DECLINED,
        PROCESSING_UNAVAILABLE,
        PAYMENT_FAILED,
        STRONG_AUTH_REQUIRED,
        SMS_FAILED,
        CORPORATE_CARD,
        NO_PHONE,
        IDEMPOTENCY_CONFLICT,
    ];

    private StatusCodes(string name, int value, int httpStatus, string uz, string en, string ru)
    {
        Name = name;
        Value = value;
        HttpStatus = httpStatus;
        Message = new Dictionary<string, string> { ["uz"] = uz, ["en"] = en, ["ru"] = ru };
    }

    public string Name { get; }

    public int Value { get; }

    public int HttpStatus { get; }

    public IReadOnlyDictionary<string, string> Message { get; }

    public static StatusCodes? ByName(string name) => All.FirstOrDefault(member => member.Name == name);

    public override string ToString() => Name;
}
