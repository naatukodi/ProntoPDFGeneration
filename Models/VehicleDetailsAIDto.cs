namespace Valuation.Api.Models
{
    // Renamed from VehicleDetailsDto → VehicleDetailsAIDto
    public class VehicleDetailsAIDto
    {
        public string RegistrationNumber { get; set; } = default!;
        public string Make { get; set; } = default!;
        public string Model { get; set; } = default!;
        public int? YearOfMfg { get; set; }
        public string? Colour { get; set; }
        public string? Fuel { get; set; } = default!;
        public int? EngineCC { get; set; }
        public decimal? IDV { get; set; }
        public DateTime? DateOfRegistration { get; set; }
        public string? City { get; set; }
        public long? Odometer { get; set; }
    }

    public class ValuationResponse
    {
        public string? RawResponse { get; set; } = "";
        public decimal? LowRange { get; set; }
        public decimal? MidRange { get; set; }
        public decimal? HighRange { get; set; }
    }
}