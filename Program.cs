using Valuation.Api.Services;
using Valuation.Api.Models;

var builder = WebApplication.CreateBuilder(args);

// --- SET QUESTPDF LICENSE HERE ---
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

// Enable detailed layout diagnostics
QuestPDF.Settings.EnableDebugging = true;

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Register your repository as before
builder.Services.AddSingleton<IValuationRepository, ValuationRepository>();

// Register PdfReportService with its HttpClient injected
builder.Services.AddHttpClient<PdfReportService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthorization();
app.MapControllers();
app.Run();
