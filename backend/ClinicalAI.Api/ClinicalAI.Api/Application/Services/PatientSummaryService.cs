using ClinicalAI.Api.Domain.Entities;
using ClinicalAI.Api.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ClinicalAI.Api.Application.Services
{
    public interface IPatientSummaryService
    {
        Task<string?> GenerateSummaryAsync(Guid patientId);
    }

    public class PatientSummaryService : IPatientSummaryService
    {
        private readonly ClinicalDbContext _db;
        private readonly IBedrockService _bedrock;
        private readonly IPiiRedactionService _redaction;
        public PatientSummaryService(ClinicalDbContext db, IBedrockService bedrock, IPiiRedactionService redaction)
        {
            _db = db;
            _bedrock = bedrock;
            _redaction = redaction;
        }

        public async Task<string?> GenerateSummaryAsync(Guid patientId)
        {
            var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == patientId);
            if (patient is null) return null;

            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var age = today.Year - patient.DateOfBirth.Year;
            if (patient.DateOfBirth > today.AddYears(-age)) age--;

            // Every free-text field goes through redaction; an empty field must still read
            // as "None recorded" rather than a blank line, so the model doesn't infer meaning
            // from the gap.
            string Field(string? value)
            {
                var redacted = _redaction.RedactFreeText(value);
                return string.IsNullOrWhiteSpace(redacted) ? "None recorded" : redacted;
            }

            var prompt = $"""
            You are a clinical decision-support assistant. You do NOT diagnose and you do NOT
            replace a physician. Given the structured patient facts below, produce:
            1. A concise clinical summary (2-3 sentences)
            2. A bullet list of relevant considerations for the treating clinician to evaluate
            3. Any notable red flags that may warrant urgent clinician attention (if none, say so)

            Do not state a diagnosis. Do not prescribe treatment. Frame everything as
            considerations for a licensed clinician to review.

            Patient facts:
            - Age: {age}
            - Sex: {patient.Sex}
            - Allergies: {Field(patient.Allergies)}
            - Current medications: {Field(patient.CurrentMedications)}
            - Reported symptoms: {Field(patient.Symptoms)}
            - Medical history: {Field(patient.MedicalHistory)}
            """;

            return await _bedrock.InvokeModelAsync(prompt);
        }
    }

}
