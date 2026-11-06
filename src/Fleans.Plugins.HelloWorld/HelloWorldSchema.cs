using Fleans.ExternalTasks.Sdk;

namespace Fleans.Plugins.HelloWorld;

/// <summary>
/// The parameter schema the Fleans management UI's BPMN editor renders when an
/// author selects the <c>hello-world</c> service-task type. Static so DI
/// registration and tests can reference the same instance.
///
/// Real plugins typically declare typed inputs (URL, timeout, etc.). This
/// example takes a single optional <c>name</c> string so the example BPMN can
/// be authored end-to-end in the editor without typing JSON.
/// </summary>
public static class HelloWorldSchema
{
    public static readonly ExternalTaskParameterSchema Default = new(new[]
    {
        new ExternalTaskParameter(
            Name: "name",
            DisplayName: "Name",
            Type: ExternalTaskParameterType.String,
            Required: false,
            Description: "Who to greet. Defaults to \"world\" when omitted. Source from a workflow variable with =myVar.",
            DefaultValue: "world")
    });
}
