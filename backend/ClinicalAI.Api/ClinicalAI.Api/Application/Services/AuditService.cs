using ClinicalAI.Api.Domain.Entities;
using ClinicalAI.Api.Infrastructure.Data;

namespace ClinicalAI.Api.Application.Services
{
    public interface IAuditService
    {
        Task LogAsync(string userEmail, string action, Guid? patientId = null, string? details = null);
    }

    public class AuditService : IAuditService
    {
        private readonly ClinicalDbContext _db;

        public AuditService(ClinicalDbContext db) => _db = db;

        public async Task LogAsync(string userEmail, string action, Guid? patientId = null, string? details = null)
        {
            _db.AuditEvents.Add(new AuditEvent
            {
                UserEmail = userEmail,
                Action = action,
                PatientId = patientId,
                Details = details
            });
            await _db.SaveChangesAsync();
        }
    }
}
