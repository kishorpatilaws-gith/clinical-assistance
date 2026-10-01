using ClinicalAI.Api.Domain.Entities;
using ClinicalAI.Api.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ClinicalAI.Api.Application.Services
{
    public interface IDocumentIndexer
    {
        /// <summary>Extract, chunk, embed and index one stored document, and record the outcome on its row.</summary>
        Task IndexAsync(Guid documentId, CancellationToken ct);
    }

    /// <summary>
    /// The work behind one queued document. It reads the original back from R2 rather
    /// than holding the upload in memory, so a document queued before a restart is
    /// indexed exactly as a fresh upload would be.
    /// </summary>
    public class DocumentIndexer : IDocumentIndexer
    {
        private readonly ClinicalDbContext _db;
        private readonly IR2StorageService _storage;
        private readonly IDocumentProcessorClient _processor;
        private readonly ILogger<DocumentIndexer> _log;

        public DocumentIndexer(ClinicalDbContext db, IR2StorageService storage,
                               IDocumentProcessorClient processor, ILogger<DocumentIndexer> log)
        {
            _db = db;
            _storage = storage;
            _processor = processor;
            _log = log;
        }

        public async Task IndexAsync(Guid documentId, CancellationToken ct)
        {
            var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == documentId, ct);
            if (doc is null)
            {
                _log.LogInformation("Document {Id} was deleted before it was indexed", documentId);
                return;
            }

            doc.Status = ProcessingStatus.Processing;
            await _db.SaveChangesAsync(ct);

            // The document's Postgres id is passed through as the vector store's
            // document_id, so the two stay in step and a re-index overwrites cleanly
            // instead of duplicating chunks.
            try
            {
                await using var content = await _storage.DownloadAsync(doc.R2Key, ct);
                var result = await _processor.IndexAsync(
                    content, doc.FileName, doc.ContentType, doc.Id, doc.DocumentType.ToString(), ct);

                doc.ExtractedText = result.Text;
                doc.Status = ProcessingStatus.Completed;
                _log.LogInformation("Indexed {FileName}: {Chars} chars, {Pages} page(s), {Chunks} chunk(s)",
                    doc.FileName, result.CharCount, result.PageCount, result.ChunksIndexed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // A shutdown is not a failure: it propagates and leaves the row Processing,
                // so the worker picks it up again on the next start.
                doc.Status = ProcessingStatus.Failed;
                doc.ExtractedText = null;
                _log.LogWarning(ex, "Indexing failed for {FileName}", doc.FileName);
            }

            doc.ProcessedAt = DateTime.UtcNow;

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Deleted while it was being indexed. The vectors just written belong to
                // nothing and would keep being cited, so clear them.
                _log.LogInformation("Document {Id} was deleted while it was being indexed", documentId);
                try { await _processor.DeleteIndexAsync(documentId, ct); }
                catch (Exception ex) { _log.LogWarning(ex, "Could not clear index for {Id}", documentId); }
            }
        }
    }
}
