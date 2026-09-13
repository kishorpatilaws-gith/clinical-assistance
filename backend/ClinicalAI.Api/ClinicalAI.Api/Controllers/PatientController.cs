using ClinicalAI.Api.Application.Services;
using ClinicalAI.Api.Domain.Entities;
using ClinicalAI.Api.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClinicalAI.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Roles = "doctor")]
    public class PatientController : ControllerBase
    {
        private readonly ClinicalDbContext _db;
        private readonly IPatientSummaryService _summaryService;
        private readonly IAuditService _audit;

        public PatientController(ClinicalDbContext db, IPatientSummaryService summaryService, IAuditService audit)
        {
            _db = db;
            _summaryService = summaryService;
            _audit = audit;
        }

        private string CurrentUserEmail =>
        User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
        ?? User.FindFirst("email")?.Value
        ?? "unknown";

        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var patients = await _db.Patients
                .Select(p => new { p.Id, p.FirstName, p.LastName, p.DateOfBirth })
                .ToListAsync();
            await _audit.LogAsync(CurrentUserEmail, "ListPatients");
            return Ok(patients);
        }

        public record CreatePatientRequest(
            string FirstName, string LastName, DateOnly DateOfBirth, string Sex,
            string? Allergies, string? CurrentMedications, string? Symptoms, string? MedicalHistory);

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreatePatientRequest req)
        {
            var patient = new Patient
            {
                FirstName = req.FirstName,
                LastName = req.LastName,
                DateOfBirth = req.DateOfBirth,
                Sex = req.Sex,
                Allergies = req.Allergies,
                CurrentMedications = req.CurrentMedications,
                Symptoms = req.Symptoms,
                MedicalHistory = req.MedicalHistory
            };
            _db.Patients.Add(patient);
            await _db.SaveChangesAsync();
            return CreatedAtAction(nameof(GetAll), new { id = patient.Id }, patient);
        }

        [HttpGet("{id}/summary")]
        public async Task<IActionResult> GetSummary(Guid id)
        {
            var summary = await _summaryService.GenerateSummaryAsync(id);
            if (summary is null) return NotFound();
            await _audit.LogAsync(CurrentUserEmail, "GenerateSummary", id);
            return Ok(new { patientId = id, summary });
        }
    }
}