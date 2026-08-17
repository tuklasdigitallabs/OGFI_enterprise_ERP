using System.Diagnostics.Metrics;

namespace Ogfi.BuildingBlocks.Observability;

public static class OgfiMetrics
{
    public const string MeterName = "OGFI.RI01";

    public static readonly Meter Meter = new(MeterName, "1.0.0");

    public static readonly Counter<long> ApiRequests =
        Meter.CreateCounter<long>("ogfi.api.requests", unit: "request");

    public static readonly Histogram<double> ApiDurationMs =
        Meter.CreateHistogram<double>("ogfi.api.duration", unit: "ms");

    public static readonly Counter<long> OutboxDispatchAttempts =
        Meter.CreateCounter<long>("ogfi.outbox.dispatch.attempts", unit: "message");

    public static readonly Counter<long> WorkerFailures =
        Meter.CreateCounter<long>("ogfi.worker.failures", unit: "failure");
}
