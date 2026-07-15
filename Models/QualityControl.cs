namespace Valuation.Api.Models;
// Models/QualityControl.cs
public class QualityControl
{
    public string OverallRating { get; set; } = default!;

    public decimal ValuationAmount { get; set; }

    public string ChassisPunch { get; set; } = default!;

    public string? Remarks { get; set; }

    public Dictionary<string, string?>? QcChecklist { get; set; }
    public Dictionary<string, string?>? QcChecklistRemarks { get; set; }
}
