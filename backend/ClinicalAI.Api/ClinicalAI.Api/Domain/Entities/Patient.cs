namespace ClinicalAI.Api.Domain.Entities;

public class Patient
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public DateOnly DateOfBirth { get; set; }
    public string Sex { get; set; } = string.Empty;
    public string? Allergies { get; set; }
    public string? CurrentMedications { get; set; }
    public string? Symptoms { get; set; }
    public string? MedicalHistory { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}