# fleans-custom-worker-example

Worked example: hosting custom-task plugins for [Fleans](https://github.com/nightBaker/fleans), the Orleans-based BPMN workflow engine.

Click **Use this template** above to start your own plugin host.

## What this is

`Fleans.CustomWorkerHost` is a minimum-viable Orleans silo that runs custom-task plugin handlers outside the Fleans engine. The silo tags itself with `Fleans:Role=Plugin` (silo-name prefix `plugin-`) and relies on Orleans' built-in `GetCompatibleSilos` to host **only** the plugin grain classes compiled into its assembly load context — engine-internal grains (Script, Condition) and other hosts' plugins never land here.

The dependency closure is intentionally tight: `Fleans.Worker` (leaf NuGet) + plugin packages + Orleans server + Aspire telemetry. **No reference to `Fleans.Application` / `Fleans.Domain` / `Fleans.Persistence`** — that structural guarantee is the whole point of Fleans' leaf-package design.

> **Breaking change (Fleans v0.3.0+).** Earlier versions of this template used `Fleans:Role=Worker`, which made plugin hosts attract engine grains they didn't sign up for. Hosts on Fleans v0.3.0+ must use `Fleans:Role=Plugin` — call `siloBuilder.AddFleansPluginHost(configuration)` and the role default + validation are handled for you.
>
> **Breaking change (Fleans v0.4.0+).** Plugin handlers must declare `[ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.<taskType>")]` as a **literal string** with the task-type baked in — not `[ImplicitStreamSubscription(WorkflowEventStreams.ExecuteCustomTaskStreamNamespace)]`. v0.4.0's per-task-type stream sharding (#566) routes events to `events.ExecuteCustomTaskEvent.<taskType>`; a handler subscribed to the bare namespace receives no events, and `AddCustomTaskPlugin<T>` throws `InvalidOperationException` at silo startup if the attribute literal doesn't match the registered task-type. See the updated example below.
>
> v0.4.0 also routes `OperationCanceledException` from the supplied grain-lifetime `CancellationToken` as a redelivery hint (NOT a failure) — plugin authors should `await` against the token (`HttpClient.SendAsync(req, ct)`, `Task.Delay(timeout, ct)`, …) so the grain can deactivate cleanly during silo shutdown. The example below shows the pattern.

## Quick start

```bash
# 1. Use this template to create your own repo, then clone it.
gh repo create my-plugin-host --template nightBaker/fleans-custom-worker-example --public
cd my-plugin-host

# 2. Run against an Orleans Redis (started by the Fleans Aspire AppHost or your own).
#    Fleans:Role defaults to "Plugin" via AddFleansPluginHost — no need to set it.
ConnectionStrings__orleans-redis="localhost:6379" dotnet run --project src/Fleans.CustomWorkerHost
```

The host claims any `<serviceTask type="rest-caller">` from BPMN definitions deployed on the engine. Trigger a workflow that uses it, and the call executes here, not on the engine silos.

## Customizing

### Add your own plugin

```csharp
// src/Fleans.MyPlugin/MyHandler.cs
using Fleans.Worker.CustomTasks;

// No [WorkerPlacement] — plugin handlers use Orleans default placement, which
// auto-filters via GetCompatibleSilos to silos that have this DLL loaded.
//
// The [ImplicitStreamSubscription] literal MUST equal
// $"events.ExecuteCustomTaskEvent.{TaskType}" — the per-task-type stream namespace
// the Fleans v0.4.0 publisher fires on (#566). Attribute arguments must be
// compile-time constants, so each plugin hard-codes its own literal — keep the
// task-type segment here in sync with the TaskType property below AND with the
// taskType passed to AddCustomTaskPlugin<T>(...). AddCustomTaskPlugin validates
// all three at silo startup and throws on drift.
[ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.my-task")]
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
        // Honor the grain-lifetime CancellationToken — v0.4.0 (#568) treats an OCE
        // thrown by the supplied token as a redelivery hint, NOT a task failure.
        // Pass `cancellationToken` to every awaitable that takes one.
        await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);

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
This matches the Fleans engine's default (v0.3.0+, unchanged in v0.4.0), wired via the third-party MIT-licensed
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
