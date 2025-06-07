namespace Valuation.Api.Models
{
    // The result we return to the client.
    public class VehicleValuation
    {
        // Low, Mid, High ranges in INR (for example: 250000–275000)
        public decimal? LowRange { get; set; }
        public decimal? MidRange { get; set; }
        public decimal? HighRange { get; set; }

        // (Optional) Raw ChatGPT text if you want to inspect it
        public string? RawResponse { get; set; }
    }
}
