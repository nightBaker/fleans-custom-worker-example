using Fleans.Application.CustomTasks;
using Fleans.Worker.CustomTasks;
using Microsoft.Extensions.DependencyInjection;

namespace Fleans.Plugins.HelloWorld;

/// <summary>
/// Wires <see cref="HelloWorldHandler"/> into a plugin-host's DI container. Call
/// from your <c>Program.cs</c> after <c>siloBuilder.AddFleansPluginHost(...)</c>:
///
/// <code>
/// builder.Services.AddHelloWorldPlugin();
/// </code>
///
/// <c>AddCustomTaskPlugin&lt;T&gt;</c> validates two contracts at silo startup
/// and throws on either failure: (a) no other plugin already claims the same
/// <paramref name="taskType"/>, and (b) <c>HelloWorldHandler</c> declares
/// <c>[ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.hello-world")]</c>
/// matching the registered task type.
/// </summary>
public static class HelloWorldServiceCollectionExtensions
{
    public static IServiceCollection AddHelloWorldPlugin(this IServiceCollection services)
    {
        services.AddCustomTaskPlugin<HelloWorldHandler>(
            taskType: HelloWorldHandler.HelloWorldTaskType,
            displayName: "Hello World",
            parameterSchema: HelloWorldSchema.Default);

        return services;
    }
}
