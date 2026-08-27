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

    /// <summary>
    /// One-off migration that pins every legacy document's reference number so the
    /// brand prefix split cannot move it. Run with ?apply=false first to see the counts.
    /// Safe to re-run: documents that already have a reference are skipped.
    /// </summary>
    [HttpPost("backfill-references")]
    public async Task<IActionResult> BackfillReferences([FromQuery] bool apply = false, CancellationToken ct = default)
    {
        var r = await _pdfService.BackfillReferenceNumbersAsync(dryRun: !apply, ct);
        return Ok(new
        {
            dryRun = !apply,
            r.Scanned,
            r.Assigned,
            r.Collisions,
            r.Written,
            r.SkippedMissingKey,
            r.Failed,
            r.Examples,
            message = apply
                ? "References written. Existing reports keep the PM- number they were printed with."
                : "Dry run only — re-send with ?apply=true to write."
        });
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