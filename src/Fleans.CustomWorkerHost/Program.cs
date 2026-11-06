using Fleans.ExternalTasks.Sdk;
using Fleans.Plugins.HelloWorld;
using Microsoft.Extensions.Hosting;

// Worked example: a minimal Fleans custom-task worker. It connects to the Fleans API over gRPC,
// receives the tasks of the types it registers and reports their outcome. Its only Fleans dependency
// is Fleans.ExternalTasks.Sdk, with no Orleans, so it can scale and deploy on its own.
//
// Set Fleans:ExternalTaskWorker:EngineUrl (env: Fleans__ExternalTaskWorker__EngineUrl) to the API.
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddFleansExternalTaskWorker();

// Add or remove .Add*Plugin() calls to pick which BPMN <serviceTask type="..."> values this worker
// serves. Each plugin is either a NuGet package or, as here, a project in this repo.
//
// To add the ready-made REST caller, reference the Fleans.Plugins.RestCaller package and call
// builder.Services.AddRestCallerPlugin();
builder.Services.AddHelloWorldPlugin();

builder.Build().Run();
