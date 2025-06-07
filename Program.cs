using Microsoft.Azure.Cosmos;
using Valuation.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// --- SET QUESTPDF LICENSE HERE ---
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

// Enable detailed layout diagnostics
QuestPDF.Settings.EnableDebugging = true;

// 3) Load & validate Cosmos settings
var cosmosCfg = builder.Configuration.GetSection("Cosmos");
var accountEndpoint = cosmosCfg["AccountEndpoint"];
var accountKey = cosmosCfg["AccountKey"];
if (string.IsNullOrWhiteSpace(accountEndpoint) || string.IsNullOrWhiteSpace(accountKey))
{
    throw new InvalidOperationException(
        "Missing Cosmos configuration. Ensure Cosmos:AccountEndpoint and Cosmos:AccountKey are set.");
}

var cosmosClient = new CosmosClient(
    accountEndpoint,
    accountKey);
var databaseId = cosmosCfg["DatabaseId"] ?? "ValuationsDb";
var containerId = cosmosCfg["ContainerId"] ?? "Valuations";

// 5) Register CosmosClient
builder.Services.AddSingleton(_ => cosmosClient);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Register your repository as before
builder.Services.AddSingleton<IValuationRepository, ValuationRepository>();

// Register PdfReportService with its HttpClient injected
builder.Services.AddHttpClient<PdfReportService>();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Valuation API V1");
    c.RoutePrefix = string.Empty;
});

app.UseAuthorization();
app.MapControllers();
app.Run();
