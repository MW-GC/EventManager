using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MW_GC.EventManager.API.Services;
using MW_GC.EventManager.Shared.Entities;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

//builder.Services
//    .AddApplicationInsightsTelemetryWorkerService()
//    .ConfigureFunctionsApplicationInsights();

var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage") ?? "UseDevelopmentStorage=true";

builder.Services.AddSingleton(new TableServiceClient(connectionString));

builder.Services.AddSingleton(sp =>
    new TableStore<GameEntity>(sp.GetRequiredService<TableServiceClient>(), "Games", "Game"));
builder.Services.AddSingleton(sp =>
    new TableStore<ActivityEntity>(sp.GetRequiredService<TableServiceClient>(), "Activities", "Activity"));
builder.Services.AddSingleton(sp =>
    new TableStore<EventEntity>(sp.GetRequiredService<TableServiceClient>(), "Events", "Event"));
builder.Services.AddSingleton(sp =>
    new TableStore<ThemeEntity>(sp.GetRequiredService<TableServiceClient>(), "Themes", "Theme"));
builder.Services.AddSingleton(sp =>
    new TableStore<HolidayEntity>(sp.GetRequiredService<TableServiceClient>(), "Holidays", "Holiday"));

builder.Services.AddSingleton<EventGenerator>();

builder.Build().Run();
