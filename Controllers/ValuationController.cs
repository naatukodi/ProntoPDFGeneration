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

        // await the new bytes‐returning method:
        byte[] pdfbytes = await _pdfService.GenerateAndShowPdf(doc);

        var fileName = $"{id}_{DateTime.UtcNow:yyyyMMdd}.pdf";
        return File(pdfbytes, "application/pdf", fileName);
    }

    [HttpGet("FinalReport/pdf")]
    public async Task<IActionResult> GetFinalReportPdf(
        Guid id,
        [FromQuery] string vehicleNumber,
        [FromQuery] string applicantContact)
    {
        // 1) Fetch the FinalReport object from your repository
        var report = await _pdfService.GetValuationDocumentAsync(id.ToString(), vehicleNumber, applicantContact);
        if (report == null)
            return NotFound();

        // 2) Generate PDF bytes
        byte[] pdfBytes = await _pdfService.GenerateAndShowPdf(report);

        // 3) Return as a file result
        string fileName = $"{vehicleNumber}_{System.DateTime.UtcNow:yyyyMMdd}.pdf";

        return File(pdfBytes, "application/pdf", fileName);
    }
}
