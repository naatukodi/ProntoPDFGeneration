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
    public IActionResult GetReport(string id)
    {
        var doc = _repository.GetValuation(id);
        if (doc == null) return NotFound();

        _pdfService.GenerateAndShowPdf(doc);

        return Ok("PDF opened in browser via Companion.");
    }
}
