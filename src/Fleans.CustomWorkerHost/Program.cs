using Fleans.Plugins.RestCaller;
using Fleans.Worker.Placement;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Orleans.Configuration;
using StackExchange.Redis;
using Universley.OrleansContrib.StreamsProvider.Redis;

// Worked example: a minimal-viable Fleans plugin worker silo.
//
// This host claims grains tagged [WorkerPlacement] (custom-task plugin handlers)
// and runs them outside the Fleans engine. The dependency closure is intentionally
// tight — Fleans.Worker (leaf NuGet) + plugin packages + Orleans server + Aspire
// telemetry — with no transitive reference to Fleans.Application / Fleans.Domain /
// Fleans.Persistence. That structural guarantee is the whole point of the leaf-
// package design.
//
// To customize: swap the .AddRestCallerPlugin() call below for your own plugins'
// registration extensions and ship the resulting container image alongside the
// Fleans engine deployment.

var builder = WebApplication.CreateBuilder(args);

// ─── Aspire-style service defaults (inlined; usually `builder.AddServiceDefaults()` ───
//     in projects that share Aspire's ServiceDefaults project).
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;
});
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddMeter("Microsoft.Orleans");
    })
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddSource("Microsoft.Orleans.Runtime")
            .AddSource("Microsoft.Orleans.Application");
    });
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
    builder.Services.AddOpenTelemetry().UseOtlpExporter();

builder.Services.AddServiceDiscovery();
builder.Services.ConfigureHttpClientDefaults(http =>
{
    http.AddStandardResilienceHandler();
    http.AddServiceDiscovery();
});

builder.Services.AddHealthChecks()
    .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(), tags: ["live"]);

// ─── Redis connection (Aspire injects ConnectionStrings:orleans-redis at runtime) ───
builder.AddKeyedRedisClient("orleans-redis");

// The Universley Redis Streams provider resolves a non-keyed IConnectionMultiplexer from DI.
// Alias the Aspire-registered keyed one so the same connection pool serves clustering,
// PubSubStore, and the stream provider — no second socket.
builder.Services.TryAddSingleton<IConnectionMultiplexer>(sp =>
    sp.GetRequiredKeyedService<IConnectionMultiplexer>("orleans-redis"));

// Redis Stream provider options (defaults match Fleans engine's FleanStreamingExtensions).
const string StreamProviderName = "StreamProvider";
builder.Services.AddOptions<HashRingStreamQueueMapperOptions>(StreamProviderName)
    .Configure(options => options.TotalQueueCount = 8);
builder.Services.AddOptions<SimpleQueueCacheOptions>(StreamProviderName);
builder.Services.AddOptions<RedisStreamReceiverOptions>(StreamProviderName)
    .Configure(options =>
    {
        options.MaxStreamLength = 1000;
        options.TrimTimeMinutes = 5;
    });

// ─── Worker-only role gate ────────────────────────────────────────────────────────────
if (string.IsNullOrEmpty(builder.Configuration["Fleans:Role"]))
    builder.Configuration["Fleans:Role"] = "Worker";

var roleRaw = builder.Configuration["Fleans:Role"]!;
var role = roleRaw.ToLowerInvariant();
if (role != "worker" && role != "combined")
    throw new InvalidOperationException(
        $"Custom worker host only supports Fleans:Role 'Worker' or 'Combined' (case-insensitive) — got '{roleRaw}'.");

var siloName = $"{role}-{Environment.MachineName}-{Guid.NewGuid():N}".ToLowerInvariant();
var orleansRedisConnection = builder.Configuration.GetConnectionString("orleans-redis");

// ─── Orleans silo wiring ──────────────────────────────────────────────────────────────
builder.UseOrleans(siloBuilder =>
{
    siloBuilder.Configure<Orleans.Configuration.SiloOptions>(o => o.SiloName = siloName);

    if (!string.IsNullOrEmpty(orleansRedisConnection))
    {
        siloBuilder.UseRedisClustering(orleansRedisConnection);
        siloBuilder.AddRedisGrainStorage("PubSubStore",
            options => options.ConfigurationOptions =
                ConfigurationOptions.Parse(orleansRedisConnection));
        siloBuilder.UseInMemoryReminderService();
    }

    // Redis Streams as the stream provider — durable, multi-silo-safe, reuses the same
    // orleans-redis connection that powers clustering + PubSubStore. Matches the Fleans
    // engine's default (v0.3.0+). For an in-process single-silo demo, swap for AddMemoryStreams.
    siloBuilder.AddPersistentStreams(StreamProviderName, RedisStreamFactory.Create, null);

    // Routes grains carrying [WorkerPlacement] (e.g. CustomTaskHandlerBase subclasses) here.
    siloBuilder.AddPlacementDirector<WorkerPlacementStrategy, WorkerPlacementDirector>();
});

// ─── Plugin registration ──────────────────────────────────────────────────────────────
// Add or remove .Add*Plugin() calls to pick which BPMN <serviceTask type="..."> values
// this host claims. Each plugin is a separate NuGet package that the host references.
builder.Services.AddRestCallerPlugin();
// builder.Services.AddYourCustomPlugin();

// ─── App ──────────────────────────────────────────────────────────────────────────────
var app = builder.Build();
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live"),
});
app.MapHealthChecks("/alive", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live"),
});
app.Run();
