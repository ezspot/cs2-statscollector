using System.Diagnostics;

namespace statsCollector.Infrastructure;

public static class Instrumentation
{
    public const string ServiceName = "cs2-statscollector";
    public const string ServiceVersion = "1.0.0";

    public static readonly ActivitySource ActivitySource = new(ServiceName, ServiceVersion);
}
