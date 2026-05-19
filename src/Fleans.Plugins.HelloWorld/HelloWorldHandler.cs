using System.Dynamic;
using Fleans.Worker.CustomTasks;
using Microsoft.Extensions.Logging;

namespace Fleans.Plugins.HelloWorld;

/// <summary>
/// Backs <c>&lt;serviceTask type="hello-world"&gt;</c>. Reads a <c>name</c> input
/// from the workflow scope and returns <c>__response.message = "Hello, {name}!"</c>.
/// The simplest possible custom-task plugin — no I/O, no external services, no
/// failure modes — so you can copy it and replace <see cref="ExecuteAsync"/> with
/// your real work without first untangling boilerplate.
///
/// The <c>[ImplicitStreamSubscription]</c> literal MUST equal
/// <c>$"events.ExecuteCustomTaskEvent.{TaskType}"</c> — the per-task-type stream
/// namespace the Fleans v0.4.0 publisher (#566) fires on. Attribute arguments are
/// compile-time constants, so each plugin hard-codes its own literal. Keep the
/// task-type segment here in sync with <see cref="TaskType"/> below AND with the
/// argument passed to <c>AddCustomTaskPlugin&lt;HelloWorldHandler&gt;(...)</c>;
/// the v0.4.0 startup validation in <c>Fleans.Worker</c> throws if any of the
/// three drift apart.
///
/// No <c>[WorkerPlacement]</c> — plugin handlers rely on Orleans default placement
/// + <c>GetCompatibleSilos</c> (assembly-loading based), which auto-routes
/// activations only to silos that have this DLL loaded.
/// </summary>
[ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.hello-world")]
public sealed class HelloWorldHandler : CustomTaskHandlerBase
{
    public const string HelloWorldTaskType = "hello-world";

    private readonly ILogger<HelloWorldHandler> _logger;

    public HelloWorldHandler(ILogger<HelloWorldHandler> logger, IGrainFactory grainFactory)
        : base(logger, grainFactory)
    {
        _logger = logger;
    }

    protected override string TaskType => HelloWorldTaskType;

    protected override Task<IDictionary<string, object?>> ExecuteAsync(
        IDictionary<string, object?> resolvedInputs,
        ExpandoObject variables,
        CustomTaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        // Honor the supplied CancellationToken even on synchronous-shaped work — v0.4.0
        // (#568) treats an OCE from this token as a redelivery hint, not a task failure.
        // For real plugins that do I/O, pass `cancellationToken` to every awaitable.
        cancellationToken.ThrowIfCancellationRequested();

        var name = (resolvedInputs.TryGetValue("name", out var raw) ? raw?.ToString() : null) ?? "world";
        var message = $"Hello, {name}!";

        _logger.LogInformation("Hello-world plugin: name={Name} → {Message} (activityInstanceId={ActivityInstanceId})",
            name, message, context.ActivityInstanceId);

        // Output mappings (<fleans:output source="=__response.message" target="greeting" />)
        // walk this dictionary — by convention plugin results land under the reserved
        // `__response` key. Returning a plain shape (no nested ExpandoObject) keeps the
        // example readable; the RestCaller plugin shows the nested ExpandoObject pattern
        // when you need it.
        IDictionary<string, object?> result = new Dictionary<string, object?>
        {
            ["__response"] = new Dictionary<string, object?>
            {
                ["message"] = message
            }
        };
        return Task.FromResult(result);
    }
}
