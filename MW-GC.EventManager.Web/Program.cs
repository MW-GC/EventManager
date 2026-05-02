using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.FluentUI.AspNetCore.Components;
using MW_GC.EventManager.Web;
using MW_GC.EventManager.Web.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var apiBase = builder.Configuration["ApiBaseUrl"] ?? builder.HostEnvironment.BaseAddress;
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(apiBase) });

builder.Services.AddFluentUIComponents();

builder.Services.AddScoped<GameService>();
builder.Services.AddScoped<ActivityService>();
builder.Services.AddScoped<EventService>();
builder.Services.AddScoped<ThemeService>();
builder.Services.AddScoped<HolidayService>();

await builder.Build().RunAsync();
