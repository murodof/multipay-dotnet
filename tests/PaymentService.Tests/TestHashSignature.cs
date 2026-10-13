using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.EntityFrameworkCore;
using PaymentService.Core.Configurations;
using PaymentService.Domain.Models;
using PaymentService.Domain.Schemas;
using PaymentService.Infrastructure.Utils;
using static PaymentService.Tests.Conftest;

namespace PaymentService.Tests;

// The Operator hash on its own: format, MAC, key rotation, payload shape and the time window.
//
// These call OperatorHash / SigningKeys directly, so a failure points at the verification rather
// than at the endpoint. The endpoint's *ordering* of the same checks is covered by the
// TestSession* files.
public sealed class TestHashSignature
{
    private static readonly DateTimeOffset NOW = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private const string NONCE = "nonce-0123456789abcdef";

    public static readonly int OrderIdLength = new PaymentDbContext(
            new DbContextOptionsBuilder<PaymentDbContext>().UseSqlServer("Server=unused").Options, TimeProvider.System)
        .Model.FindEntityType(typeof(PaymentSession))!.FindProperty(nameof(PaymentSession.OrderId))!.GetMaxLength()!.Value;

    private static JsonObject Payload(params (string Key, object? Value)[] fields)
    {
        var values = new Dictionary<string, object?>
        {
            ["order_id"] = "order-1",
            ["phone"] = PHONE,
            ["amount"] = 5_000_000L,
            ["ts"] = NOW.ToUnixTimeSeconds(),
            ["nonce"] = NONCE,
        };
        foreach (var (key, value) in fields)
        {
            values[key] = value;
        }
        var payload = new JsonObject();
        foreach (var (key, value) in values)
        {
            if (!ReferenceEquals(value, DROP))
            {
                payload[key] = System.Text.Json.JsonSerializer.SerializeToNode(value);
            }
        }
        return payload;
    }

    private static object Verify(string? raw, DateTimeOffset now, string secretKeys = $"{KID}:{SECRET}") =>
        OperatorHash.VerifyHash(raw, now, SigningKeys.Parse(secretKeys), OrderIdLength);

    private static bool Rejected(object result, string? field = null) =>
        result is ErrorResponse error
        && error.Code == "HASH_INVALID"
        && (field is null || error.Error["details"]?.ToJsonString() == new JsonObject { ["field"] = field }.ToJsonString());

    // ─── MAC ───

    [Fact]
    public void A_signed_hash_verifies_back_to_its_payload()
    {
        var result = Verify(OperatorHash.SignHash(Payload(), KID, SECRET), NOW);

        Assert.Equal(new HashPayload(KID, "order-1", PHONE, 5_000_000, NOW.ToUnixTimeSeconds(), NONCE), result);
    }

    [Fact]
    public void A_payload_swapped_under_a_valid_signature_is_refused()
    {
        var parts = OperatorHash.SignHash(Payload(), KID, SECRET).Split('.');
        var forgedBody = OperatorHash.SignHash(Payload(("amount", 100_000)), KID, SECRET).Split('.')[1];

        Assert.True(Rejected(Verify($"{parts[0]}.{forgedBody}.{parts[2]}", NOW)));
    }

    [Fact]
    public void A_hash_signed_with_an_unconfigured_secret_is_refused() =>
        Assert.True(Rejected(Verify(OperatorHash.SignHash(Payload(), KID, OTHER_SECRET), NOW)));

    [Fact]
    public void An_unknown_kid_is_refused() =>
        Assert.True(Rejected(Verify(OperatorHash.SignHash(Payload(), "k9", SECRET), NOW)));

    // Relabelling a k1 hash as k2 must not verify. The MAC input is `<kid>.<body>`, so a hash
    // signed for one key cannot be replayed through the other while both are configured.
    [Fact]
    public void The_kid_is_covered_by_the_mac()
    {
        var parts = OperatorHash.SignHash(Payload(), KID, SECRET).Split('.');

        Assert.True(Rejected(Verify($"k2.{parts[1]}.{parts[2]}", NOW, $"{KID}:{SECRET},k2:{SECRET}")));
    }

    // C# cannot monkeypatch CryptographicOperations.FixedTimeEquals the way the Python test patches
    // hmac.compare_digest, so the same guarantee is read from the syntax tree: VerifyHash makes
    // exactly one constant-time comparison, no return sits between the key lookup and it, and the
    // "is the key usable" test is evaluated after the comparison rather than short-circuiting it.
    [Fact]
    public void The_signature_is_compared_in_constant_time()
    {
        var method = VerifyHashSyntax();
        var comparisons = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(invocation => invocation.Expression.ToString().EndsWith("FixedTimeEquals", StringComparison.Ordinal))
            .ToList();

        Assert.Single(comparisons);
        var lookup = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(invocation => invocation.Expression.ToString().EndsWith("GetValueOrDefault", StringComparison.Ordinal));
        var earlyReturns = method.DescendantNodes().OfType<ReturnStatementSyntax>()
            .Where(statement => statement.SpanStart > lookup.SpanStart && statement.SpanStart < comparisons[0].SpanStart);
        Assert.Empty(earlyReturns);
        var condition = comparisons[0].Ancestors().OfType<BinaryExpressionSyntax>().Last();
        Assert.EndsWith("!usable", condition.ToString(), StringComparison.Ordinal);
    }

    // Returning early for an unknown kid would make that path measurably faster and tell a caller
    // which kids exist. The dummy-key MAC keeps both paths doing the same work.
    [Fact]
    public void An_unknown_kid_still_goes_through_the_full_comparison()
    {
        var method = VerifyHashSyntax();
        var expected = method.DescendantNodes().OfType<VariableDeclaratorSyntax>().Single(variable => variable.Identifier.Text == "expected");

        Assert.Contains("RandomNumberGenerator.GetBytes(32)", expected.Initializer!.Value.ToString(), StringComparison.Ordinal);
        Assert.True(Rejected(Verify(OperatorHash.SignHash(Payload(), "k9", SECRET), NOW)));
    }

    // ─── key rotation ───

    [Fact]
    public void During_rotation_both_keys_verify()
    {
        var expiry = Iso(NOW + TimeSpan.FromHours(24));
        var keys = $"k2:{OTHER_SECRET},{KID}:{SECRET}@{expiry}";

        Assert.False(Rejected(Verify(OperatorHash.SignHash(Payload(), "k2", OTHER_SECRET), NOW, keys)));
        Assert.False(Rejected(Verify(OperatorHash.SignHash(Payload(), KID, SECRET), NOW, keys)));
    }

    [Fact]
    public void The_outgoing_key_stops_verifying_after_its_expiry()
    {
        var expiry = NOW + TimeSpan.FromHours(24);
        var keys = $"k2:{OTHER_SECRET},{KID}:{SECRET}@{Iso(expiry)}";
        var signed = OperatorHash.SignHash(Payload(), KID, SECRET);

        Assert.False(Rejected(Verify(signed, expiry, keys)));
        Assert.True(Rejected(Verify(signed, expiry + TimeSpan.FromSeconds(1), keys)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" , ")]
    [InlineData($"bad kid:{SECRET}")]
    [InlineData($"{KID}:too-short")]
    [InlineData($"{KID}:{SECRET},{KID}:{OTHER_SECRET}")]
    [InlineData($"{KID}:{SECRET}@2026-09-16T12:00:00")]
    [InlineData($"{KID}:{SECRET}@not-a-date")]
    [InlineData($"{KID}:k1-test-secret-01234,{SECRET}")]
    public void Misconfigured_keys_fail_loudly(string raw) =>
        Assert.NotNull(SigningKeys.Parse(raw).Error);

    // The message lands in startup logs; an entry missing its `kid:` would otherwise print the secret.
    [Theory]
    [InlineData(SECRET)]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345")]
    [InlineData("k1abcdefghijklmnopqrstuvwxyz0123")]
    public void A_config_error_never_echoes_a_secret(string raw)
    {
        var error = SigningKeys.Parse(raw).Error;

        Assert.NotNull(error);
        Assert.DoesNotContain(raw, error, StringComparison.Ordinal);
    }

    // ─── format ───

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("a.b")]
    [InlineData("a.b.c.d")]
    [InlineData($"{KID}.e30.not base64!")]
    [InlineData($"{KID}.???.AAAA")]
    [InlineData("ключ.e30.AAAA")]
    public void Malformed_hashes_are_invalid(string? raw) => Assert.True(Rejected(Verify(raw, NOW)));

    [Fact]
    public void An_overlong_hash_is_invalid() => Assert.True(Rejected(Verify(new string('x', 2049), NOW)));

    // 32 bytes encode to 43 base64url characters with two spare bits. Setting them decodes to the
    // same bytes, so accepting it would give one signature several spellings.
    [Fact]
    public void A_non_canonical_signature_encoding_is_refused()
    {
        var parts = OperatorHash.SignHash(Payload(), KID, SECRET).Split('.');
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var tweaked = parts[2][..^1] + alphabet[alphabet.IndexOf(parts[2][^1], StringComparison.Ordinal) ^ 1];

        Assert.Equal(
            Convert.FromBase64String(parts[2].Replace('-', '+').Replace('_', '/') + "="),
            Convert.FromBase64String(tweaked.Replace('-', '+').Replace('_', '/') + "="));
        Assert.True(Rejected(Verify($"{parts[0]}.{parts[1]}.{tweaked}", NOW)));
    }

    // ─── payload shape (only reachable behind a valid MAC) ───

    public static TheoryData<string, object?, string> WrongShapes => new()
    {
        { "order_id", "has space", "order_id" },
        { "order_id", new string('x', 65), "order_id" },
        { "order_id", 42, "order_id" },
        { "order_id", DROP, "order_id" },
        { "phone", 998901234567, "phone" },
        { "phone", DROP, "phone" },
        { "amount", "5000000", "amount" },
        { "amount", true, "amount" },
        { "amount", 50000.5, "amount" },
        { "amount", JsonNode.Parse("1.0"), "amount" },
        { "amount", JsonNode.Parse("1e3"), "amount" },
        { "ts", NOW.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), "ts" },
        { "ts", 1_000_000_000_000L, "ts" },
        { "nonce", "too-short", "nonce" },
        { "nonce", "slashes/are/not/allowed", "nonce" },
        { "nonce", DROP, "nonce" },
    };

    [Theory]
    [MemberData(nameof(WrongShapes))]
    public void A_signed_payload_of_the_wrong_shape_names_the_field(string key, object? value, string field) =>
        Assert.True(Rejected(Verify(OperatorHash.SignHash(Payload((key, value)), KID, SECRET), NOW), field));

    // `order_id` is capped by the column's MaxLength on the model, not by a second constant.
    [Fact]
    public void The_order_id_limit_is_the_column_width()
    {
        Assert.Equal(64, OrderIdLength);
        Assert.False(Rejected(Verify(OperatorHash.SignHash(Payload(("order_id", new string('x', 64))), KID, SECRET), NOW)));
    }

    [Theory]
    [InlineData("[1, 2]")]
    [InlineData("not json")]
    [InlineData("\xff\xfe")]
    public void A_signed_body_that_is_not_a_json_object_is_invalid(string rawBody)
    {
        var bytes = rawBody == "\xff\xfe" ? [0xff, 0xfe] : Encoding.UTF8.GetBytes(rawBody);
        var body = OperatorHash.B64UrlEncode(bytes);
        var signed = $"{KID}.{body}.{OperatorHash.B64UrlEncode(OperatorHash.Mac(Encoding.UTF8.GetBytes(SECRET), KID, body))}";

        Assert.True(Rejected(Verify(signed, NOW), "payload"));
    }

    // A lone surrogate escape is valid JSON to Python and fails the field's pattern there; here
    // reading the string throws, which must be the same HASH_INVALID and not a 500.
    [Theory]
    [InlineData("order_id")]
    [InlineData("nonce")]
    public void A_lone_surrogate_in_a_signed_field_is_invalid(string field) =>
        Assert.True(Rejected(Verify(WithLoneSurrogate(field), NOW), field));

    // Python only asks that `phone` is a string here; the phone check further on refuses it.
    [Fact]
    public void A_lone_surrogate_phone_is_left_to_the_phone_check() =>
        Assert.IsType<HashPayload>(Verify(WithLoneSurrogate("phone"), NOW));

    private static string WithLoneSurrogate(string field)
    {
        var payload = Payload().ToJsonString();
        var value = System.Text.Json.Nodes.JsonNode.Parse(payload)![field]!.ToJsonString();
        var bytes = Encoding.UTF8.GetBytes(payload.Replace($"\"{field}\":{value}", $"\"{field}\":\"\\ud800\"", StringComparison.Ordinal));
        var body = OperatorHash.B64UrlEncode(bytes);
        return $"{KID}.{body}.{OperatorHash.B64UrlEncode(OperatorHash.Mac(Encoding.UTF8.GetBytes(SECRET), KID, body))}";
    }

    // ─── time window ───

    [Theory]
    [InlineData(0, true)]
    [InlineData(-300, true)]
    [InlineData(300, true)]
    [InlineData(-301, false)]
    [InlineData(301, false)]
    public void The_timestamp_window_is_300_seconds_either_way(int offset, bool fresh)
    {
        var item = new HashPayload(KID, "o", PHONE, 1, NOW.ToUnixTimeSeconds() + offset, NONCE);

        Assert.Equal(fresh, OperatorHash.IsFresh(item, NOW, 300));
    }

    // ─── cross-implementation: hashes signed by the Python sign_hash() ───

    public static TheoryData<string> PythonCases => new(PythonFixture()["cases"]!.AsArray().Select(item => item!["name"]!.GetValue<string>()));

    [Theory]
    [MemberData(nameof(PythonCases))]
    public void A_hash_signed_by_the_python_reference_verifies_identically(string name)
    {
        var fixture = PythonFixture();
        var item = fixture["cases"]!.AsArray().Single(entry => entry!["name"]!.GetValue<string>() == name)!;
        var now = DateTimeOffset.Parse(fixture["now"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        var result = Verify(item["hash"]!.GetValue<string>(), now, fixture["secret_keys"]!.GetValue<string>());
        var expected = item["expected"]!;

        if (expected["error"] is { } error)
        {
            var actual = Assert.IsType<ErrorResponse>(result);
            Assert.Equal(error.GetValue<string>(), actual.Code);
            Assert.Equal(expected["details"]?.ToJsonString(), actual.Error["details"]?.ToJsonString());
        }
        else
        {
            var payload = Assert.IsType<HashPayload>(result);
            var want = expected["payload"]!;
            Assert.Equal(want["kid"]!.GetValue<string>(), payload.Kid);
            Assert.Equal(want["order_id"]!.GetValue<string>(), payload.OrderId);
            Assert.Equal(want["phone"]!.GetValue<string>(), payload.Phone);
            Assert.Equal(want["ts"]!.GetValue<long>(), payload.Ts);
            Assert.Equal(want["nonce"]!.GetValue<string>(), payload.Nonce);
            if (want["amount"]!.GetValueKind() == System.Text.Json.JsonValueKind.Number)
            {
                Assert.Equal(want["amount"]!.GetValue<long>(), payload.Amount);
            }
            Assert.Equal(expected["fresh"]!.GetValue<bool>(), OperatorHash.IsFresh(payload, now, 300));
        }
    }

    [Theory]
    [MemberData(nameof(PythonCases))]
    public void The_dotnet_sign_hash_produces_the_python_bytes(string name)
    {
        var item = PythonFixture()["cases"]!.AsArray().Single(entry => entry!["name"]!.GetValue<string>() == name)!;
        var payload = item["payload"]!.AsObject();

        Assert.Equal(
            item["hash"]!.GetValue<string>(),
            OperatorHash.SignHash(payload, item["kid"]!.GetValue<string>(), item["secret"]!.GetValue<string>()));
    }

    private static JsonObject PythonFixture() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "python_signed_hashes.json")))!.AsObject();

    private static string Iso(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    private static MethodDeclarationSyntax VerifyHashSyntax()
    {
        var path = Path.Combine(TestArchitecture.SourceRoot, "Infrastructure", "Utils", "OperatorHash.cs");
        return CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot()
            .DescendantNodes().OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.Text == "VerifyHash");
    }
}
