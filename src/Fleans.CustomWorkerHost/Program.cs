using Fleans.Plugins.HelloWorld;
using Fleans.Worker.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Orleans.Configuration;
using StackExchange.Redis;
using Universley.OrleansContrib.StreamsProvider.Redis;

// Worked example: a minimal-viable Fleans plugin worker silo.
//
// This host runs custom-task plugin handlers outside the Fleans engine. Orleans'
// default placement + GetCompatibleSilos (assembly-loading) automatically routes a
// plugin grain only to silos that have the concrete handler DLL — so this host
// runs ONLY the plugins it has compiled in, never the engine's Script/Condition
// grains, and never other hosts' plugins.
//
// The dependency closure is intentionally tight: Fleans.Worker (leaf NuGet) +
// plugin packages + Orleans server + Aspire telemetry. No transitive reference to
// Fleans.Application / Fleans.Domain / Fleans.Persistence — that structural
// guarantee is the whole point of the leaf-package design.
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

var orleansRedisConnection = builder.Configuration.GetConnectionString("orleans-redis");

// ─── Orleans silo wiring ──────────────────────────────────────────────────────────────
builder.UseOrleans(siloBuilder =>
{
    // Validates Fleans:Role (must be Plugin or Combined; rejects Core/Worker), stamps the
    // silo name as `plugin-<machine>-<guid>` so the engine's WorkerPlacementDirector
    // excludes this host from Script/Condition routing, and registers the worker placement
    // director locally for cluster-wide placement decisions.
    siloBuilder.AddFleansPluginHost(builder.Configuration);

    // Workaround for nightBaker/fleans#703 on Fleans v0.5.0 and earlier: plugin host silos
    // must NOT advertise as Orleans client gateways. Without this override, Fleans.Web's
    // Orleans client round-robins among advertised gateways and intermittently picks this
    // plugin silo as its gateway. The plugin silo's assembly load context lacks
    // Fleans.Application, so calls to engine grains (IWorkflowInstanceGrain, ...)
    // forwarded through it throw "Unable to load type Fleans.Application.Grains.IWorkflowInstanceGrain".
    // Engine-side fix in AddFleansPluginHost lands in the Fleans release after v0.5.0;
    // delete this Configure block once you upgrade past it.
    siloBuilder.Configure<Orleans.Configuration.EndpointOptions>(o => o.GatewayPort = 0);

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
});

// ─── Plugin registration ──────────────────────────────────────────────────────────────
// Add or remove .Add*Plugin() calls to pick which BPMN <serviceTask type="..."> values
// this host claims. Each plugin is either a separate NuGet package or — as here — a
// project authored in this repo and ProjectReference'd from the host csproj.
//
// HelloWorld is the worked-example plugin shipped in this template (see
// ../Fleans.Plugins.HelloWorld/). To swap in the pre-built REST caller plugin, add
// `<PackageReference Include="Fleans.Plugins.RestCaller" Version="0.5.0" />` to the
// host csproj and replace this line with `builder.Services.AddRestCallerPlugin();`.
builder.Services.AddHelloWorldPlugin();
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
