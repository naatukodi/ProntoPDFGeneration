// Models/WorkflowStep.cs
namespace Valuation.Api.Models
{
    public class WorkflowStep
    {
        public int StepOrder { get; set; }
        public int TemplateStepId { get; set; }
        public string AssignedToRole { get; set; } = default!;
        public string Status { get; set; } = "Pending";    // Pending, InProgress, Completed
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
    }
}
