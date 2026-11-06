using System.Dynamic;
using Fleans.ExternalTasks.Sdk;
using Microsoft.Extensions.Logging;

namespace Fleans.Plugins.HelloWorld;

/// <summary>
/// Backs <c>&lt;serviceTask type="hello-world"&gt;</c>. Reads a <c>name</c> input and returns
/// <c>__response.message = "Hello, {name}!"</c>. The simplest possible handler, with no I/O, no
/// external services and no failure modes, so you can copy it and replace <see cref="ExecuteAsync"/>
/// with your real work.
/// </summary>
public sealed partial class HelloWorldHandler(ILogger<HelloWorldHandler> logger) : ExternalTaskHandler
{
    public const string HelloWorldTaskType = "hello-world";

    public override Task<IDictionary<string, object?>> ExecuteAsync(
        IDictionary<string, object?> resolvedInputs,
        ExpandoObject variables,
        ExternalTaskContext context,
        CancellationToken cancellationToken)
    {
        // An OperationCanceledException from this token means "the task will be delivered again",
        // not a failure. Handlers that do I/O pass it to every awaitable.
        cancellationToken.ThrowIfCancellationRequested();

        var name = (resolvedInputs.TryGetValue("name", out var raw) ? raw?.ToString() : null) ?? "world";
        var message = $"Hello, {name}!";

        LogGreeted(name, context.ActivityInstanceId);

        // Output mappings (<fleans:output source="=__response.message" target="greeting" />) read
        // this dictionary; by convention results land under the `__response` key.
        IDictionary<string, object?> result = new Dictionary<string, object?>
        {
            ["__response"] = new Dictionary<string, object?>
            {
                ["message"] = message
            }
        };
        return Task.FromResult(result);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Greeted {Name} (activity instance {ActivityInstanceId})")]
    private partial void LogGreeted(string name, Guid activityInstanceId);
}
