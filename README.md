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

> ## ⚠️ Known engine-side blockers on Fleans v0.4.0
>
> When an external plugin host runs a plugin that is **not** also bundled in the engine images (`fleans-api` / `fleans-worker`), two engine-side gaps block end-to-end completion. The HelloWorld plugin in this template is exactly that shape — handler activates correctly on the plugin host, but the workflow doesn't complete until both engine fixes ship.
>
> - **[nightBaker/fleans#627](https://github.com/nightBaker/fleans/issues/627)** — `AddFleansPluginHost` only registers `WorkerPlacementStrategy`; needs `CorePlacementStrategy` too so the plugin host can target `[CorePlacement]` grains (specifically `CustomTaskCatalogGrain` for plugin registration). Symptom: `Catalog Register failed ... Could not resolve placement strategy CorePlacementStrategy for grain type customtaskcatalog`.
> - **[nightBaker/fleans#628](https://github.com/nightBaker/fleans/issues/628)** — `ExpandoObjectSurrogateConverter` lives in `Fleans.Domain` (engine-only), but the plugin host needs it to serialize the `ExpandoObject variables` argument of `IWorkflowInstanceCallback`. Symptom: `CodecNotFoundException: Could not find a copier for type System.Dynamic.ExpandoObject` on the response callback. Fix: move the surrogate to `Fleans.Domain.Abstractions` (in every plugin host's closure).
>
> Both issues are hidden by the canonical `Fleans.Plugins.RestCaller` smoke (the engine workers also bundle that plugin, so Orleans routes activations there). They surface immediately the moment an operator authors a plugin that's only on the external host.
>
> Until v0.4.1 ships these fixes, the workaround is to register the same plugin's NuGet package on the engine `fleans-worker` image as well — defeats the isolation intent of the template, but unblocks end-to-end.

## Quick start

```bash
# 1. Use this template to create your own repo, then clone it.
gh repo create my-plugin-host --template nightBaker/fleans-custom-worker-example --public
cd my-plugin-host

# 2. Run against an Orleans Redis (started by the Fleans Aspire AppHost or your own).
#    Fleans:Role defaults to "Plugin" via AddFleansPluginHost — no need to set it.
ConnectionStrings__orleans-redis="localhost:6379" dotnet run --project src/Fleans.CustomWorkerHost
```

The host ships with one worked-example plugin — `Fleans.Plugins.HelloWorld` (see [`src/Fleans.Plugins.HelloWorld/`](src/Fleans.Plugins.HelloWorld)) — which claims `<serviceTask type="hello-world">` and returns `__response.message = "Hello, {name}!"`. Deploy a BPMN that uses it on the engine, trigger a workflow, and the call executes here, not on the engine silos.

To host the pre-built REST-call plugin (`Fleans.Plugins.RestCaller`, ships with the Fleans engine) instead of (or alongside) Hello World, add `<PackageReference Include="Fleans.Plugins.RestCaller" Version="0.4.0" />` to `Fleans.CustomWorkerHost.csproj` and call `builder.Services.AddRestCallerPlugin();` in `Program.cs`.

## The Hello World plugin (the worked example)

The template ships with the smallest-possible plugin so the structure is obvious. Three files in `src/Fleans.Plugins.HelloWorld/`:

- **`HelloWorldHandler.cs`** — the handler grain. Reads `name` from inputs, returns `__response.message`.
- **`HelloWorldSchema.cs`** — the parameter schema the Fleans editor renders when an author selects this task type.
- **`HelloWorldServiceCollectionExtensions.cs`** — the DI extension `AddHelloWorldPlugin()` the host calls.

The handler in full:

```csharp
// src/Fleans.Plugins.HelloWorld/HelloWorldHandler.cs
using System.Dynamic;
using Fleans.Worker.CustomTasks;
using Microsoft.Extensions.Logging;

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
[ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.hello-world")]
public sealed class HelloWorldHandler : CustomTaskHandlerBase
{
    public const string HelloWorldTaskType = "hello-world";

    private readonly ILogger<HelloWorldHandler> _logger;

    public HelloWorldHandler(ILogger<HelloWorldHandler> logger, IGrainFactory grainFactory)
        : base(logger, grainFactory) { _logger = logger; }

    protected override string TaskType => HelloWorldTaskType;

    protected override Task<IDictionary<string, object?>> ExecuteAsync(
        IDictionary<string, object?> resolvedInputs,
        ExpandoObject variables,
        CustomTaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        // Honor the supplied CancellationToken — v0.4.0 (#568) treats an OCE from
        // this token as a redelivery hint, NOT a task failure. For real plugins
        // that do I/O, pass cancellationToken to every awaitable.
        cancellationToken.ThrowIfCancellationRequested();

        var name = (resolvedInputs.TryGetValue("name", out var raw) ? raw?.ToString() : null) ?? "world";
        var message = $"Hello, {name}!";

        IDictionary<string, object?> result = new Dictionary<string, object?>
        {
            ["__response"] = new Dictionary<string, object?> { ["message"] = message }
        };
        return Task.FromResult(result);
    }
}
```

The DI registration:

```csharp
// src/Fleans.Plugins.HelloWorld/HelloWorldServiceCollectionExtensions.cs
public static IServiceCollection AddHelloWorldPlugin(this IServiceCollection services) =>
    services.AddCustomTaskPlugin<HelloWorldHandler>(
        taskType: HelloWorldHandler.HelloWorldTaskType,
        displayName: "Hello World",
        parameterSchema: HelloWorldSchema.Default);
```

The Program.cs wiring is one line:

```csharp
builder.Services.AddHelloWorldPlugin();
```

### Try it end-to-end

Deploy this BPMN on the Fleans engine:

```xml
<bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"
                  xmlns:fleans="https://fleans.io/schema/bpmn/1.0">
  <bpmn:process id="hello-smoke" isExecutable="true">
    <bpmn:startEvent id="start" />
    <bpmn:serviceTask id="greet">
      <bpmn:extensionElements>
        <fleans:taskDefinition type="hello-world" />
        <fleans:ioMapping>
          <fleans:input  source="Alice"            target="name" />
          <fleans:output source="=__response.message" target="greeting" />
        </fleans:ioMapping>
      </bpmn:extensionElements>
    </bpmn:serviceTask>
    <bpmn:endEvent id="end" />
    <bpmn:sequenceFlow id="f1" sourceRef="start" targetRef="greet" />
    <bpmn:sequenceFlow id="f2" sourceRef="greet" targetRef="end" />
  </bpmn:process>
</bpmn:definitions>
```

Start an instance via `POST /Execution/start {"WorkflowId":"hello-smoke"}` and inspect the result — `greeting` will be `"Hello, Alice!"`, executed on this plugin host (EventId 4035 `Custom-task handler activated` lands in the plugin-host logs, not the engine's).

## Customizing

### Add your own plugin

Use the Hello World project as a template:

```bash
cp -r src/Fleans.Plugins.HelloWorld src/Fleans.Plugins.MyTask
# rename namespaces + handler class + task-type literal in the 3 files
```

The three things that MUST stay in lock-step (v0.4.0 startup validation enforces this):

1. **`[ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.<taskType>")]` literal** on the handler.
2. **`TaskType` property** returning the same string.
3. **`taskType:` argument** to `AddCustomTaskPlugin<T>(...)`.

Then in `Program.cs`:

```csharp
builder.Services.AddHelloWorldPlugin();
builder.Services.AddMyTaskPlugin(); // <- add this
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

You should see only `Fleans.Application.Abstractions`, `Fleans.Domain.Abstractions`, `Fleans.Worker`, and your plugin packages or in-repo plugin projects (this template's `Fleans.Plugins.HelloWorld`). **No `Fleans.Application` or `Fleans.Domain`** — those stay engine-internal.

## License

PolyForm Internal Use 1.0.0 — matches the [Fleans engine license](https://github.com/nightBaker/fleans/blob/main/LICENSE). Use within your organization; for redistribution or commercial offerings, contact the engine maintainers.

## Related

- **[Fleans engine](https://github.com/nightBaker/fleans)** — the BPMN workflow engine this host plugs into.
- **[Custom tasks documentation](https://nightbaker.github.io/fleans/concepts/custom-tasks/)** — full plugin-authoring guide.
- **[Self-hosting guides](https://nightbaker.github.io/fleans/guides/)** — docker-compose and Helm deployment.
