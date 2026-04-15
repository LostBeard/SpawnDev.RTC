using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SpawnDev.BlazorJS;
using SpawnDev.RTC.Demo;
using SpawnDev.RTC.Demo.UnitTests;

// Print build timestamp
var buildTimestamp = typeof(Program).Assembly
    .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
    .OfType<System.Reflection.AssemblyMetadataAttribute>()
    .FirstOrDefault(a => a.Key == "BuildTimestamp")?.Value ?? "unknown";
Console.WriteLine($"SpawnDev.RTC.Demo build: {buildTimestamp}");

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Ensure WebGPU verbose logging is disabled during Blazor unit tests
builder.Services.AddBlazorJSRuntime();

// Unit tests
builder.Services.AddSingleton<WasmRTCTests>();

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

await builder.Build().BlazorJSRunAsync();
