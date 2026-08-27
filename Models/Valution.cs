// Define ValuationDocument if it does not exist elsewhere

namespace Valuation.Api.Models;

public class ValuationDocument
{
    public string? id { get; set; }
    public string TypeOfVal { get; set; } = "ValuationReport";
    public string ReportRequestedBy { get; set; } = "Valuation Team";
    // Assigned once, on first PDF generation, then never recomputed — the QR code and
    // the blob path are both keyed off it, so a printed report has to keep resolving.
    public string ReferenceNumber { get; set; } = "";

    // Which company the case belongs to: "vehga" or "pronto". Null on every document
    // created before multi-brand shipped, which is Vehga by definition.
    public string? Brand { get; set; }

    public string dateOfValuation { get; set; } = DateTime.UtcNow.ToString("yyyy-MM-dd");
    
    // --- Added properties for the PDF generation links ---
    public Dictionary<string, string> VideoUrls { get; set; } = new();
    public string? ImagesUrl { get; set; }

    public Stakeholder? Stakeholder { get; set; }
    // Add other properties as needed
    public string? CompositeKey { get; set; }
    public string? VehicleNumber { get; set; }
    public string? ApplicantContact { get; set; }
    public string? VehicleSegment { get; set; }
    public List<Document>? Documents { get; set; }
    public VehicleDetailsDto? VehicleDetails { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public InspectionDetails? InspectionDetails { get; set; }
    public QualityControl? QualityControl { get; set; }
    public ValuationResponse? ValuationResponse { get; set; }
    public Dictionary<string, string> PhotoUrls { get; set; } = new();

    // Keys (from PhotoUrls) QC chose to include in the PDF gallery page.
    // Null/empty means "not set" — fall back to including everything available.
    public List<string>? SelectedGalleryPhotos { get; set; }

    public List<WorkflowStep>? Workflow { get; set; }
    public string? Status { get; set; } = "Open";
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? CompletedBy { get; set; }
    public string? AssignedTo { get; set; }
    public string? AssignedToRole { get; set; }
}

public class Stakeholder
{
    public string? Name { get; set; }
    public string? ExecutiveName { get; set; }
    public string? ExecutiveContact { get; set; }
    public string? ExecutiveWhatsapp { get; set; }
    public string? ExecutiveEmail { get; set; }
    public Applicant? Applicant { get; set; }
    public string? VehicleNumber { get; set; }
    public string? VehicleSegment { get; set; }
    public string? ValuationType { get; set; }
    public List<Document>? Documents { get; set; }
}

public class Applicant
{
    public string? Name { get; set; }
    public string? Contact { get; set; }
}

public class Document
{
    public string? Type { get; set; }
    public string? FilePath { get; set; }
    public DateTime UploadedAt { get; set; }
}
