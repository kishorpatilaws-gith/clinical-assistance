namespace ClinicalAI.Api.Domain.Entities
{
    public class AuditEvent
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string UserEmail { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;       // e.g. "ViewPatient", "GenerateSummary"
        public Guid? PatientId { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string? Details { get; set; }
    }
}
