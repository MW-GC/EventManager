using Azure.Data.Tables;
using Azure.Identity;
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

builder.Services.AddSingleton(CreateTableServiceClient());

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

// Builds the Table Storage client from the AzureWebJobsStorage configuration. Prefers the
// identity-based scheme (AzureWebJobsStorage__accountName + managed identity) used in Azure;
// falls back to a connection string (e.g. Azurite "UseDevelopmentStorage=true") for local dev.
static TableServiceClient CreateTableServiceClient()
{
    var accountName = Environment.GetEnvironmentVariable("AzureWebJobsStorage__accountName");
    if (!string.IsNullOrWhiteSpace(accountName))
    {
        var endpoint = new Uri($"https://{accountName}.table.core.windows.net");
        return new TableServiceClient(endpoint, new DefaultAzureCredential());
    }

    var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage") ?? "UseDevelopmentStorage=true";
    return new TableServiceClient(connectionString);
}
