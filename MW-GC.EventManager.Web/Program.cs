using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MW_GC.EventManager.Web;
using MW_GC.EventManager.Web.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var apiBase = builder.Configuration["ApiBaseUrl"] ?? builder.HostEnvironment.BaseAddress;
// Every registration lives in AppServices, where a test builds the same services (LibraryClientTests).
builder.Services.AddAppServices(apiBase);

await builder.Build().RunAsync();
