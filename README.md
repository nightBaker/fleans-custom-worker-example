# fleans-custom-worker-example

Worked example: a custom-task worker for [Fleans](https://github.com/nightBaker/fleans), the BPMN workflow engine.

Click **Use this template** above to start your own worker.

## What this is

`Fleans.CustomWorkerHost` is a minimal generic host that connects to a Fleans API over gRPC, receives the tasks of the `<serviceTask type="...">` values it registers, and reports their outcome. It joins no cluster and shares no database, stream or Redis with the engine: its whole configuration is the API's URL.

The dependency closure is `Fleans.ExternalTasks.Sdk` plus your plugins. **No Orleans and no engine projects**, and CI guards it. Anything that can speak the [gRPC protocol](https://github.com/nightBaker/fleans/blob/main/src/Fleans/Fleans.ExternalTasks.Sdk/Protos/external_task_worker.proto) can do the same in another language.

> **Breaking change (Fleans 0.11.0).** Plugin silos are gone (nightBaker/fleans#1013). `CustomTaskHandlerBase`, `AddCustomTaskPlugin<T>`, `AddFleansPluginHost` and the `Fleans.Worker` package no longer exist. To move a handler over: derive from `ExternalTaskHandler`, drop `[ImplicitStreamSubscription]` and the grain constructor arguments, throw `ExternalTaskFailedException` instead of `CustomTaskFailedActivityException`, register with `AddExternalTaskHandler<T>`, and replace the host's silo wiring with `AddFleansExternalTaskWorker()`.

## Quick start

```bash
# 1. Use this template to create your own repo, then clone it.
gh repo create my-worker --template nightBaker/fleans-custom-worker-example --public
cd my-worker

# 2. Run it against a Fleans API.
Fleans__ExternalTaskWorker__EngineUrl="http://localhost:8080" dotnet run --project src/Fleans.CustomWorkerHost
```

The worker ships with one worked example, `Fleans.Plugins.HelloWorld` (see [`src/Fleans.Plugins.HelloWorld/`](src/Fleans.Plugins.HelloWorld)), which claims `<serviceTask type="hello-world">` and returns `__response.message = "Hello, {name}!"`. Once connected, the type appears in the engine's custom-task catalog and the editor's palette.

To run the ready-made REST caller as well, add a `PackageReference` to `Fleans.Plugins.RestCaller` and call `builder.Services.AddRestCallerPlugin();` in `Program.cs`.

## The Hello World plugin (the worked example)

Three files in `src/Fleans.Plugins.HelloWorld/`:

- **`HelloWorldHandler.cs`**: the handler. Reads `name` from inputs, returns `__response.message`.
- **`HelloWorldSchema.cs`**: the parameter schema the Fleans editor renders when an author selects this task type.
- **`HelloWorldServiceCollectionExtensions.cs`**: the DI extension `AddHelloWorldPlugin()` the host calls.

The core of the handler:

```csharp
public sealed partial class HelloWorldHandler(ILogger<HelloWorldHandler> logger) : ExternalTaskHandler
{
    public const string HelloWorldTaskType = "hello-world";

    public override Task<IDictionary<string, object?>> ExecuteAsync(
        IDictionary<string, object?> resolvedInputs,
        ExpandoObject variables,
        ExternalTaskContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var name = (resolvedInputs.TryGetValue("name", out var raw) ? raw?.ToString() : null) ?? "world";

        IDictionary<string, object?> result = new Dictionary<string, object?>
        {
            ["__response"] = new Dictionary<string, object?> { ["message"] = $"Hello, {name}!" }
        };
        return Task.FromResult(result);
    }
}
```

The registration:

```csharp
public static IServiceCollection AddHelloWorldPlugin(this IServiceCollection services) =>
    services.AddExternalTaskHandler<HelloWorldHandler>(
        taskType: HelloWorldHandler.HelloWorldTaskType,
        displayName: "Hello World",
        parameterSchema: HelloWorldSchema.Default);
```

And `Program.cs`:

```csharp
builder.Services.AddFleansExternalTaskWorker();
builder.Services.AddHelloWorldPlugin();
```

## What a handler must get right

- **Be idempotent.** Delivery is at-least-once: after a dropped connection or a timeout the task runs again, possibly on another worker. Key external side effects by `context.ActivityInstanceId`, which stays the same across deliveries.
- **Pass on the `CancellationToken`.** It fires when the engine stops waiting, the connection drops or the host stops. The task is then delivered again, so an `OperationCanceledException` from it is not a failure.
- **Fail with a code.** `throw new ExternalTaskFailedException("DECLINED", "card declined")` reaches an error boundary declaring `DECLINED`. Any other exception fails the task with code `500`.
- **Return JSON-serializable values.** Inputs, variables and results cross the wire as JSON.
- **Report later, if the work outlives the handler.** Return `Deferred` to leave the activity open with no timeout, then settle it with `POST /Execution/complete-activity` or `fail-activity`. A worker registered as `scheme:*` gets every type starting with `scheme:`; those wildcards aren't listed in the catalog.

A task type with no connected worker waits for one, within the engine's stream delivery window.

## Try it end-to-end

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

Start an instance via `POST /Execution/start {"WorkflowId":"hello-smoke"}` and inspect the result — `greeting` will be `"Hello, Alice!"`, executed by this worker (its log shows `Greeted Alice`).

## Customizing

### Add your own plugin

Use the Hello World project as a template:

```bash
cp -r src/Fleans.Plugins.HelloWorld src/Fleans.Plugins.MyTask
# rename namespaces + handler class + task type in the 3 files
```

Then in `Program.cs`:

```csharp
builder.Services.AddHelloWorldPlugin();
builder.Services.AddMyTaskPlugin(); // <- add this
```

### Worker settings

Bound from `Fleans:ExternalTaskWorker` (environment: `Fleans__ExternalTaskWorker__*`):

| Setting | Default | Meaning |
|---|---|---|
| `EngineUrl` | *required* | The Fleans API base URL. |
| `WorkerName` | `{machine}-{process id}` | The name the catalog lists this worker under; unique per process. |
| `UseGrpcWeb` | `true` | gRPC-Web over HTTP/1.1, which works against any API endpoint. `false` for an HTTP/2 endpoint. |
| `AccessToken` | none | Bearer token, for an API with authentication on. |
| `MaxConcurrentTasks` | `32` | Tasks run at the same time in this process. |
| `ReconnectDelay` | `00:00:05` | Pause before re-subscribing after the connection drops. |

Run as many replicas as you like: tasks are shared among the connected workers of a type.

## Verifying the dependency closure

```bash
dotnet list src/Fleans.CustomWorkerHost package --include-transitive | grep -iE 'fleans|orleans'
```

You should see only `Fleans.ExternalTasks.Sdk`, and no `Microsoft.Orleans.*`.

## License

PolyForm Internal Use 1.0.0 — matches the [Fleans engine license](https://github.com/nightBaker/fleans/blob/main/LICENSE). Use within your organization; for redistribution or commercial offerings, contact the engine maintainers.

## Related

- **[Fleans engine](https://github.com/nightBaker/fleans)** — the BPMN workflow engine this worker connects to.
- **[Custom tasks documentation](https://nightbaker.github.io/fleans/concepts/custom-tasks/)** — the full authoring guide.
- **[Self-hosting guides](https://nightbaker.github.io/fleans/guides/)** — docker-compose and Helm deployment.
