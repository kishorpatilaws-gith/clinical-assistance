using Microsoft.EntityFrameworkCore;
using ClinicalAI.Api.Domain.Entities;

namespace ClinicalAI.Api.Infrastructure.Data;

public class ClinicalDbContext : DbContext
{
    public ClinicalDbContext(DbContextOptions<ClinicalDbContext> options) : base(options) { }

    public DbSet<Patient> Patients => Set<Patient>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<Document> Documents => Set<Document>();
}