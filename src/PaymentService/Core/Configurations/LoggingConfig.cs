using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Serilog;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace PaymentService.Core.Configurations;

public static class LoggingConfig
{
    public static string MaskQueryString(string query) => string.Join(
        "&",
        query.Split('&').Select(part => part.Length == 0 ? part : part.Contains('=') ? $"{part[..part.IndexOf('=')]}=***" : "***"));

    public static string MaskUrls(string text) =>
        Regex.Replace(text, "\\?([^\\s\"'#]+)", match => "?" + MaskQueryString(match.Groups[1].Value));

    public static LoggerConfiguration SetupLogging(
        LoggerConfiguration configuration,
        Settings settings,
        TextWriter? stream = null,
        string? level = null,
        string? logFile = null)
    {
        var minimum = LogLevels.Parse(level ?? settings.LOG_LEVEL) ?? LogEventLevel.Information;
        var formatter = new QueryStringMaskingFormatter();
        configuration
            .MinimumLevel.Is(minimum)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.DataProtection", LogEventLevel.Error)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning);
        configuration = stream is null
            ? configuration.WriteTo.Console(formatter)
            : configuration.WriteTo.Sink(new TextWriterSink(formatter, stream));
        var filePath = logFile is null && stream is null ? settings.LOG_FILE : logFile;
        if (!string.IsNullOrEmpty(filePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
            configuration = configuration.WriteTo.File(
                formatter,
                filePath,
                fileSizeLimitBytes: settings.LOG_MAX_BYTES,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: settings.LOG_BACKUP_COUNT + 1,
                encoding: new UTF8Encoding(false));
        }
        return configuration;
    }
}

public sealed class QueryStringMaskingFormatter : ITextFormatter
{
    private readonly MessageTemplateTextFormatter message = new("{Message:lj}", CultureInfo.InvariantCulture);

    public void Format(LogEvent logEvent, TextWriter output)
    {
        var rendered = new StringWriter(CultureInfo.InvariantCulture);
        message.Format(logEvent, rendered);
        var name = logEvent.Properties.TryGetValue("SourceContext", out var context) && context is ScalarValue { Value: string source }
            ? source
            : "root";
        var level = logEvent.Level switch
        {
            LogEventLevel.Verbose or LogEventLevel.Debug => "DEBUG",
            LogEventLevel.Information => "INFO",
            LogEventLevel.Warning => "WARNING",
            LogEventLevel.Error => "ERROR",
            _ => "CRITICAL",
        };
        output.Write(logEvent.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        output.Write($" {level} {name}: ");
        output.Write(LoggingConfig.MaskUrls(rendered.ToString()));
        output.Write('\n');
        if (logEvent.Exception is not null)
        {
            output.Write(LoggingConfig.MaskUrls(logEvent.Exception.ToString()));
            output.Write('\n');
        }
    }
}

public sealed class TextWriterSink(ITextFormatter formatter, TextWriter output) : Serilog.Core.ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        lock (output)
        {
            formatter.Format(logEvent, output);
            output.Flush();
        }
    }
}
