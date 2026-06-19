using Microsoft.AspNetCore.Mvc;
using Valuation.Api.Services;

namespace Valuation.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ValuationController : ControllerBase
{
    private readonly IValuationRepository _repository;
    private readonly PdfReportService _pdfService;

    public ValuationController(IValuationRepository repository, PdfReportService pdfService)
    {
        _repository = repository;
        _pdfService = pdfService;
    }

    [HttpGet("{id}/report")]
    public async Task<IActionResult> GetReport(string id)
    {
        var doc = _repository.GetValuation(id);
        if (doc == null)
            return NotFound();

        // Updated method name here
        byte[] pdfbytes = await _pdfService.GeneratePdfAsync(doc);

        var fileName = $"{id}_{DateTime.UtcNow:yyyyMMdd}.pdf";
        return File(pdfbytes, "application/pdf", fileName);
    }

    [HttpGet("FinalReport/pdf")]
    public async Task<IActionResult> GetFinalReportPdf(
        Guid id,
        [FromQuery] string vehicleNumber,
        [FromQuery] string applicantContact)
    {
        var report = await _pdfService.GetValuationDocumentAsync(id.ToString(), vehicleNumber, applicantContact);
        if (report == null)
            return NotFound();

        // Updated method name here
        byte[] pdfBytes = await _pdfService.GeneratePdfAsync(report);

        string fileName = $"{vehicleNumber}_{System.DateTime.UtcNow:yyyyMMdd}.pdf";
        return File(pdfBytes, "application/pdf", fileName);
    }
}