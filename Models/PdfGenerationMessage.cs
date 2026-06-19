namespace Valuation.Api.Models
{
    public class PdfGenerationMessage
    {
        public string ValuationId { get; set; } = string.Empty;
        public string VehicleNumber { get; set; } = string.Empty;
        public string ApplicantContact { get; set; } = string.Empty;
    }
}