using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SeleneFrontend;
using SeleneFrontend.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.RootComponents.Add<App>("#app");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<IlluminationRepository>();
builder.Services.AddScoped<MissionConfigState>();
builder.Services.AddScoped<AnalysisService>();

await builder.Build().RunAsync();
