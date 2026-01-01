using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using statsCollector.Config;
using statsCollector.Infrastructure;

namespace statsCollector.Infrastructure.Observability;

public static class Bootstrapper
{
    public static void InitializeSerilog(PluginConfig config)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(GetSerilogLevel(config.LogLevel))
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Version", Instrumentation.ServiceVersion)
            .WriteTo.Console()
            .WriteTo.File("logs/statscollector-.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();
    }

    public static (TracerProvider? Tracer, MeterProvider? Meter) InitializeOpenTelemetry()
    {
        var resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(Instrumentation.ServiceName, serviceVersion: Instrumentation.ServiceVersion);

        var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(resourceBuilder)
            .AddSource(Instrumentation.ServiceName)
            .AddSource("MySqlConnector")
            .AddOtlpExporter()
            .Build();

        var meterProvider = Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(resourceBuilder)
            .AddMeter(Instrumentation.ServiceName)
            .AddRuntimeInstrumentation()
            .AddOtlpExporter()
            .Build();

        return (tracerProvider, meterProvider);
    }

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
