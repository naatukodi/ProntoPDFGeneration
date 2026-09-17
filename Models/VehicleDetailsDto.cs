namespace Valuation.Api.Models
{
    public class VehicleDetailsDto
    {
        // These must match your ValuationDocument.VehicleDetails properties:
        public string? RegistrationNumber { get; set; }
        public string? Make { get; set; }
        public string? Model { get; set; }
        public int? MonthOfMfg { get; set; }
        public int? YearOfMfg { get; set; }
        public string? BodyType { get; set; }
        public string? ChassisNumber { get; set; }
        public string? EngineNumber { get; set; }
        public string? Colour { get; set; }
        public string? Fuel { get; set; }
        public string? OwnerName { get; set; }
        public string? PresentAddress { get; set; }
        public string? PermanentAddress { get; set; }
        public bool? Hypothecation { get; set; }
        public string? Insurer { get; set; }
        public DateTime? DateOfRegistration { get; set; }
        public string? ClassOfVehicle { get; set; }
        public int? EngineCC { get; set; }
        public double? GrossVehicleWeight { get; set; }
        public string? OwnerSerialNo { get; set; }
        public int? SeatingCapacity { get; set; }
        public string? InsurancePolicyNo { get; set; }
        public DateTime? InsuranceValidUpTo { get; set; }
        public decimal? IDV { get; set; }
        public string? PermitNo { get; set; }
        public DateTime? PermitValidUpTo { get; set; }
        public string? FitnessNo { get; set; }
        public DateTime? FitnessValidTo { get; set; }
        public bool? BacklistStatus { get; set; }
        public bool? RcStatus { get; set; }

        /// <summary>
        /// The RC status VAHAN actually returned, e.g. "ACTIVE", "SUSPENDED", "NOC ISSUED".
        ///
        /// <see cref="RcStatus"/> is a bool the QC checklist reads and is hardcoded true;
        /// printing it would render "TRUE". This carries the text so the report can state
        /// what the registering authority said. Free text from VAHAN — print it, do not
        /// branch on it. Must exist in BOTH copies of this DTO (Valuation.Api and
        /// ProntoPDFGeneration): they are hand-synced, and if only one changes everything
        /// still compiles while the report silently prints "---".
        /// </summary>
        public string? RcStatusText { get; set; }
        public string? StencilTraceUrl { get; set; }
        public string? ChassisNoPhotoUrl { get; set; }
        public int? Odometer { get; set; }

        public IFormFile? StencilTrace { get; set; }
        public IFormFile? ChassisNoPhoto { get; set; }

        public List<Document>? Documents { get; set; } = new List<Document>();
        // For images, you can extend with IFormFile if you want uploads here
        // public IFormFile? StencilTrace { get; set; }
        // public IFormFile? ChassisPhoto { get; set; }

        // ── newly added fields ────────────────────────────────────────────────
        public string? Rto { get; set; }
        public string? Lender { get; set; }
        public decimal? ExShowroomPrice { get; set; }
        public string? CategoryCode { get; set; }
        public string? NormsType { get; set; }
        public string? MakerVariant { get; set; }

        public string? PollutionCertificateNumber { get; set; }
        public DateTime? PollutionCertificateUpto { get; set; }

        public string? PermitType { get; set; }
        public DateTime? PermitIssued { get; set; }
        public DateTime? PermitFrom { get; set; }

        public DateTime? TaxUpto { get; set; }
        public string? TaxPaidUpto { get; set; }

        public DateTime? ManufacturedDate { get; set; }
    }
}
