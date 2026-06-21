using System.Diagnostics;

namespace statsCollector.Infrastructure;

public static class Instrumentation
{
    public const string ServiceName = "cs2-statscollector";
    public const string ServiceVersion = "3.1.0";

    // Lightweight distributed-tracing source. StartActivity is a near-free no-op when no listener is
    // attached, so these calls cost nothing in production while leaving a hook for diagnostics.
    // Metrics (System.Diagnostics.Metrics.Meter) were removed: the plugin runs in-process with no
    // scrape endpoint/exporter, so per-event Counter.Add calls only added tag-boxing allocations on
    // hot paths with no consumer. Structured logging (Serilog) is the production observability path.
    public static readonly ActivitySource ActivitySource = new(ServiceName, ServiceVersion);
}
