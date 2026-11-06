using System.IO;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using statsCollector.Config;

namespace statsCollector.Infrastructure.Observability;

public static class Bootstrapper
{
    // Returns a private logger rather than assigning Serilog's static Log.Logger: CounterStrikeSharp shares its
    // Serilog assembly with every plugin, so that static (and closing it on unload) is process-wide. The path is
    // absolute because the file sink resolves relative paths against the CS2 process's working directory.
    public static Logger CreateLogger(PluginConfig config, string logDirectory) =>
        new LoggerConfiguration()
            .MinimumLevel.Is(GetSerilogLevel(config.LogLevel))
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Version", Plugin.Version)
            .WriteTo.Console()
            .WriteTo.File(Path.Join(logDirectory, "statscollector-.log"), rollingInterval: RollingInterval.Day)
            .CreateLogger();

    private static LogEventLevel GetSerilogLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => LogEventLevel.Verbose,
        LogLevel.Debug => LogEventLevel.Debug,
        LogLevel.Information => LogEventLevel.Information,
        LogLevel.Warning => LogEventLevel.Warning,
        LogLevel.Error => LogEventLevel.Error,
        LogLevel.Critical => LogEventLevel.Fatal,
        _ => LogEventLevel.Information
    };
}
