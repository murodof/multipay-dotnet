using Microsoft.Extensions.Logging;
using PaymentService.Core.Configurations;
using Serilog;
using Serilog.Extensions.Logging;
using static PaymentService.Tests.Conftest;

namespace PaymentService.Tests;

// The iframe URL carries the signed hash in its query string. It must never reach a log file.
public sealed class TestLoggingConfig
{
    private const string SIGNED = $"{KID}.eyJvcmRlcl9pZCI6IjEifQ.c2lnbmF0dXJlLXNpZ25hdHVyZS1zaWduYXR1cmUtc2ln";

    private static (Microsoft.Extensions.Logging.ILoggerFactory Factory, StringWriter Stream, Serilog.Core.Logger Logger) Setup(string? file = null)
    {
        var stream = new StringWriter();
        var settings = TestPanPepperSettings.SettingsFor([]);
        var logger = LoggingConfig.SetupLogging(new LoggerConfiguration(), settings, stream, "INFO", file).CreateLogger();
        return (new SerilogLoggerFactory(logger), stream, logger);
    }

    [Theory]
    [InlineData($"hash={SIGNED}", "hash=***")]
    [InlineData("hash=abc&lang=uz", "hash=***&lang=***")]
    [InlineData(SIGNED, "***")]
    [InlineData("a=&b", "a=***&***")]
    [InlineData("", "")]
    public void Every_query_value_is_masked_and_keys_are_kept(string query, string masked) =>
        Assert.Equal(masked, LoggingConfig.MaskQueryString(query));

    [Fact]
    public void An_access_line_never_carries_the_hash()
    {
        var (factory, stream, logger) = Setup();
        factory.CreateLogger("PaymentService.Access").LogInformation(
            "{Client} - \"{Method} {Path} HTTP/{Version}\" {Status}", "203.0.113.10:50000", "GET", $"/?hash={SIGNED}&lang=uz", "1.1", 200);
        logger.Dispose();

        var output = stream.ToString();
        Assert.DoesNotContain(SIGNED, output, StringComparison.Ordinal);
        Assert.Contains("\"GET /?hash=***&lang=*** HTTP/1.1\" 200", output, StringComparison.Ordinal);
    }

    // The placeholder case is the dangerous one: in Python, masking `msg` alone turned `?hash=%s`
    // into `?hash=***`, formatting then failed, and logging's error report printed the raw argument
    // to stderr. The formatter masks the rendered message, so nothing is left to format afterwards.
    [Fact]
    public void A_url_inside_any_message_is_masked()
    {
        var (factory, stream, logger) = Setup();
        var stderr = new StringWriter();
        var previous = Console.Error;
        Console.SetError(stderr);
        try
        {
            var log = factory.CreateLogger("app.anything");
            log.LogWarning("redirecting to https://pay.example.uz/?hash={Hash}", SIGNED);
            log.LogWarning($"inline https://pay.example.uz/checkout?hash={SIGNED}");
            Serilog.Debugging.SelfLog.Enable(Console.Error);
            logger.Dispose();
        }
        finally
        {
            Serilog.Debugging.SelfLog.Disable();
            Console.SetError(previous);
        }

        var output = stream.ToString();
        Assert.DoesNotContain(SIGNED, output, StringComparison.Ordinal);
        Assert.Equal(2, output.Split("?hash=***").Length - 1);
        Assert.DoesNotContain(SIGNED, stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_exception_text_is_masked_too()
    {
        var (factory, stream, logger) = Setup();
        factory.CreateLogger("app.anything").LogError(new InvalidOperationException($"GET /?hash={SIGNED}"), "failed");
        logger.Dispose();

        Assert.DoesNotContain(SIGNED, stream.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_line_format_matches_python_logging()
    {
        var (factory, stream, logger) = Setup();
        factory.CreateLogger("app.anything").LogWarning("hello");
        logger.Dispose();

        Assert.Matches(@"\A\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} WARNING app\.anything: hello\n\z", stream.ToString());
    }

    [Fact]
    public void File_log_is_created_and_masks_hash()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"multipay-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "nested", "payment.log");
        try
        {
            var (factory, _, logger) = Setup(path);
            factory.CreateLogger("PaymentService.Access").LogInformation("GET /?hash={Hash} HTTP/1.1", SIGNED);
            logger.Dispose();

            var contents = File.ReadAllText(path);
            Assert.Contains("GET /?hash=*** HTTP/1.1", contents, StringComparison.Ordinal);
            Assert.DoesNotContain(SIGNED, contents, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
