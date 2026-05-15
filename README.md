# fleans-custom-worker-example

Worked example: hosting custom-task plugins for [Fleans](https://github.com/nightBaker/fleans), the Orleans-based BPMN workflow engine.

Click **Use this template** above to start your own plugin host.

## What this is

`Fleans.CustomWorkerHost` is a minimum-viable Orleans silo that claims grains tagged `[WorkerPlacement]` (your custom-task plugin handlers) and runs them alongside the Fleans engine. The dependency closure is intentionally tight: `Fleans.Worker` (leaf NuGet) + plugin packages + Orleans server + Aspire telemetry. **No reference to `Fleans.Application` / `Fleans.Domain` / `Fleans.Persistence`** — that structural guarantee is the whole point of Fleans' leaf-package design.

## Quick start

```bash
# 1. Use this template to create your own repo, then clone it.
gh repo create my-plugin-host --template nightBaker/fleans-custom-worker-example --public
cd my-plugin-host

# 2. Run against an Orleans Redis (started by the Fleans Aspire AppHost or your own).
ConnectionStrings__orleans-redis="localhost:6379" dotnet run --project src/Fleans.CustomWorkerHost
```

The host claims any `<serviceTask type="rest-caller">` from BPMN definitions deployed on the engine. Trigger a workflow that uses it, and the call executes here, not on the engine silos.

## Customizing

### Add your own plugin

```csharp
// src/Fleans.MyPlugin/MyHandler.cs
using Fleans.Application.Abstractions.Events;
using Fleans.Worker.CustomTasks;
using Fleans.Worker.Placement;

[ImplicitStreamSubscription(WorkflowEventStreams.ExecuteCustomTaskStreamNamespace)]
[WorkerPlacement]
public sealed class MyHandler : CustomTaskHandlerBase
{
    public MyHandler(ILogger<MyHandler> logger, IGrainFactory factory) : base(logger, factory) { }

    protected override string TaskType => "my-task";

    protected override async Task<IDictionary<string, object?>> ExecuteAsync(
        IDictionary<string, object?> resolvedInputs,
        ExpandoObject variables,
        CustomTaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        // your work
        return new Dictionary<string, object?>
        {
            ["__response"] = new { ok = true }
        };
    }
}

public static class MyPluginRegistration
{
    public static IServiceCollection AddMyPlugin(this IServiceCollection services) =>
        services.AddCustomTaskPlugin<MyHandler>(taskType: "my-task", displayName: "My Task");
}
```

Then in `Program.cs`:

```csharp
builder.Services.AddRestCallerPlugin();
builder.Services.AddMyPlugin(); // <- add this
```

### Streaming providers

The template ships with **Redis Streams** as the default — durable, multi-silo-safe, and reuses
the same `orleans-redis` connection that already powers Orleans clustering and `PubSubStore`.
This matches the Fleans engine's default (v0.3.0+), wired via the third-party MIT-licensed
[`Universley.OrleansContrib.StreamsProvider.Redis`](https://github.com/MichaelSL/Universley.OrleansContrib.StreamsProvider.Redis)
package.

**Production requirement:** the Redis instance must have persistence (AOF or RDB) enabled,
otherwise a Redis restart loses in-flight stream messages (same caveat operators already have
for `PubSubStore`).

To swap providers:

```csharp
// In-process Memory (single-silo demo, lossy on restart — debug only)
siloBuilder.AddMemoryStreams("StreamProvider");

// Kafka (separate cluster)
siloBuilder.AddKafkaStreams("StreamProvider",
    builder.Configuration.GetSection("Fleans:Streaming:Kafka"));

// Azure Queue Storage (Azurite locally, real Azure in prod)
siloBuilder.AddAzureQueueStreaming("StreamProvider",
    builder.Configuration.GetSection("Fleans:Streaming:AzureQueue"));
```

Add the corresponding `Microsoft.Orleans.Streaming.Kafka` / `Microsoft.Orleans.Streaming.AzureStorage` NuGet to the csproj when swapping.

The Fleans engine **must** be configured with the same stream provider — see [self-hosting docs](https://nightbaker.github.io/fleans/guides/self-host-docker-compose) for the engine-side config.

## Verifying the leaf-package promise

```bash
dotnet list package --include-transitive --project src/Fleans.CustomWorkerHost | grep -i fleans
```

You should see only `Fleans.Application.Abstractions`, `Fleans.Domain.Abstractions`, `Fleans.Worker`, and your plugin packages. **No `Fleans.Application` or `Fleans.Domain`** — those stay engine-internal.

## License

PolyForm Internal Use 1.0.0 — matches the [Fleans engine license](https://github.com/nightBaker/fleans/blob/main/LICENSE). Use within your organization; for redistribution or commercial offerings, contact the engine maintainers.

## Related

- **[Fleans engine](https://github.com/nightBaker/fleans)** — the BPMN workflow engine this host plugs into.
- **[Custom tasks documentation](https://nightbaker.github.io/fleans/concepts/custom-tasks/)** — full plugin-authoring guide.
- **[Self-hosting guides](https://nightbaker.github.io/fleans/guides/)** — docker-compose and Helm deployment.
