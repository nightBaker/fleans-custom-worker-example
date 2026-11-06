using Fleans.ExternalTasks.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace Fleans.Plugins.HelloWorld;

/// <summary>
/// Registers <see cref="HelloWorldHandler"/> in a worker, next to <c>AddFleansExternalTaskWorker()</c>:
/// <code>
/// builder.Services.AddHelloWorldPlugin();
/// </code>
/// </summary>
public static class HelloWorldServiceCollectionExtensions
{
    public static IServiceCollection AddHelloWorldPlugin(this IServiceCollection services) =>
        services.AddExternalTaskHandler<HelloWorldHandler>(
            taskType: HelloWorldHandler.HelloWorldTaskType,
            displayName: "Hello World",
            parameterSchema: HelloWorldSchema.Default);
}
