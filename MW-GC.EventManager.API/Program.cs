using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MW_GC.EventManager.API.Services;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services.AddSingleton(CreateTableServiceClient());

// One store per table (TableNames), over the client above.
builder.Services.AddTableStores();
// The anonymous Event feedback page (#101): its own tables, and a per-instance like rate limit.
builder.Services.AddSingleton<FeedbackStore>();
builder.Services.AddSingleton(new LikeRateLimiter(TimeProvider.System));
// The daily timer that extends every Series to the horizon (#204, ADR 0002).
builder.Services.AddSingleton<SeriesOccurrences>();
builder.Services.AddSingleton<SeriesExtender>();

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
