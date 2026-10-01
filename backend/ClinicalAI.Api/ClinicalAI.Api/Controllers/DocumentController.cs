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
    public class DocumentController : ControllerBase
    {
        private static readonly string[] AllowedContentTypes =
        {
            "application/pdf",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "application/msword",
            "text/plain",
            "text/markdown",
            "text/csv"
        };

        private static readonly string[] AllowedExtensions = { ".pdf", ".docx", ".doc", ".txt", ".md", ".csv" };

        private readonly ClinicalDbContext _db;
        private readonly IR2StorageService _storage;
        private readonly IDocumentProcessorClient _processor;
        private readonly IDocumentIndexingQueue _queue;
        private readonly IAuditService _audit;
        private readonly IConfiguration _config;
        private readonly ILogger<DocumentController> _log;

        public DocumentController(ClinicalDbContext db, IR2StorageService storage,
                                  IDocumentProcessorClient processor, IDocumentIndexingQueue queue,
                                  IAuditService audit, IConfiguration config, ILogger<DocumentController> log)
        {
            _db = db;
            _storage = storage;
            _processor = processor;
            _queue = queue;
            _audit = audit;
            _config = config;
            _log = log;
        }

        private string CurrentUserEmail =>
            User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
            ?? User.FindFirst("email")?.Value
            ?? "unknown";

        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            // Extracted text can be very large, so the list projection omits it.
            var docs = await _db.Documents
                .OrderByDescending(d => d.UploadedAt)
                .Select(d => new
                {
                    d.Id,
                    d.FileName,
                    d.DocumentType,
                    d.Status,
                    d.UploadedAt,
                    d.ProcessedAt,
                    CharCount = d.ExtractedText != null ? d.ExtractedText.Length : 0
                })
                .ToListAsync();

            await _audit.LogAsync(CurrentUserEmail, "ListDocuments");
            return Ok(docs);
        }

        [HttpGet("{id:guid}")]
        public async Task<IActionResult> GetById(Guid id)
        {
            var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == id);
            if (doc is null) return NotFound();

            await _audit.LogAsync(CurrentUserEmail, "ViewDocument", details: $"DocumentId: {id}");
            return Ok(doc);
        }

        [HttpPost("upload")]
        [RequestSizeLimit(30 * 1024 * 1024)]
        // NB: no [FromForm] on the IFormFile parameter. It binds from form data anyway,
        // and the explicit attribute makes Swashbuckle's generator throw, which breaks
        // /swagger/v1/swagger.json for the whole API - not just this endpoint.
        public async Task<IActionResult> Upload(IFormFile file, [FromForm] DocumentType documentType,
                                                CancellationToken ct)
        {
            if (file is null || file.Length == 0)
                return BadRequest(new { error = "No file was uploaded." });

            var maxBytes = _config.GetValue<long>("Uploads:MaxBytes", 30 * 1024 * 1024);
            if (file.Length > maxBytes)
                return BadRequest(new { error = $"File exceeds the {maxBytes / (1024 * 1024)}MB limit." });

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            var contentType = file.ContentType?.Split(';')[0].Trim().ToLowerInvariant() ?? string.Empty;

            if (!AllowedContentTypes.Contains(contentType) && !AllowedExtensions.Contains(ext))
                return BadRequest(new { error = $"Unsupported file type '{file.ContentType}'. Allowed: PDF, DOCX, TXT." });

            if (!Enum.IsDefined(documentType))
                return BadRequest(new { error = $"Unknown document type '{documentType}'." });

            // 1. Store the original in R2 first. If extraction later fails, the file is
            //    still safe and the row can be reprocessed.
            string r2Key;
            await using (var upload = file.OpenReadStream())
            {
                r2Key = await _storage.UploadAsync(upload, file.FileName, contentType, documentType, ct);
            }

            var doc = new Document
            {
                FileName = file.FileName,
                R2Key = r2Key,
                DocumentType = documentType,
                ContentType = contentType,
                Status = ProcessingStatus.Pending
            };
            _db.Documents.Add(doc);
            await _db.SaveChangesAsync(ct);

            // 2. Extract, chunk, embed and index in the background. A whole book takes
            //    many minutes on the processor, so the request returns as soon as the
            //    original is safe and the row exists; the row moves to Completed or
            //    Failed when the worker is done with it.
            _queue.Enqueue(doc.Id);

            await _audit.LogAsync(CurrentUserEmail, "UploadDocument",
                details: $"DocumentId: {doc.Id}, Status: {doc.Status}, Key: {r2Key}");

            return AcceptedAtAction(nameof(GetById), new { id = doc.Id }, new
            {
                doc.Id,
                doc.FileName,
                doc.DocumentType,
                doc.Status,
                doc.UploadedAt,
                doc.ProcessedAt,
                CharCount = 0
            });
        }

        /// <summary>
        /// Re-index every document. Each file is re-read from R2 rather than re-chunking
        /// the stored text, so the result is identical to a fresh upload. Queued like an
        /// upload: one large book alone outlasts any request.
        /// </summary>
        [HttpPost("reindex")]
        public async Task<IActionResult> Reindex(CancellationToken ct)
        {
            // Rows already queued or being indexed are left alone - queueing them again
            // would only index them twice.
            var docs = await _db.Documents
                .Where(d => d.Status != ProcessingStatus.Pending && d.Status != ProcessingStatus.Processing)
                .OrderBy(d => d.UploadedAt)
                .ToListAsync(ct);

            foreach (var doc in docs) doc.Status = ProcessingStatus.Pending;
            await _db.SaveChangesAsync(ct);
            foreach (var doc in docs) _queue.Enqueue(doc.Id);

            await _audit.LogAsync(CurrentUserEmail, "ReindexDocuments",
                details: $"{docs.Count} document(s) queued");

            return Accepted(new { queued = docs.Count });
        }

        [HttpDelete("{id:guid}")]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == id, ct);
            if (doc is null) return NotFound();

            // Clear the vectors first. A leftover index keeps citing text that no
            // longer exists, which is worse than an orphaned object in R2.
            try { await _processor.DeleteIndexAsync(id, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "Could not clear index for {Id}", id); }

            try { await _storage.DeleteAsync(doc.R2Key, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "Could not delete R2 object {Key}", doc.R2Key); }

            _db.Documents.Remove(doc);
            await _db.SaveChangesAsync(ct);
            await _audit.LogAsync(CurrentUserEmail, "DeleteDocument", details: $"DocumentId: {id}");

            return NoContent();
        }
    }
}
