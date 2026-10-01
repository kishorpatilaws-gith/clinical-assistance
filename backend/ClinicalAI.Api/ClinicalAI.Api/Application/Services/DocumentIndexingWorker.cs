using ClinicalAI.Api.Domain.Entities;
using ClinicalAI.Api.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ClinicalAI.Api.Application.Services
{
    /// <summary>
    /// Indexes queued documents one at a time. A large book takes many minutes on the
    /// processor - far longer than an upload request should stay open - so the upload
    /// only stores the file and queues it, and this does the rest.
    ///
    /// One at a time on purpose: the processor embeds on the CPU, so running documents
    /// side by side would only make each of them slower.
    /// </summary>
    public class DocumentIndexingWorker : BackgroundService
    {
        private readonly IDocumentIndexingQueue _queue;
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<DocumentIndexingWorker> _log;

        public DocumentIndexingWorker(IDocumentIndexingQueue queue, IServiceScopeFactory scopes,
                                      ILogger<DocumentIndexingWorker> log)
        {
            _queue = queue;
            _scopes = scopes;
            _log = log;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await RequeueUnfinishedAsync(stoppingToken);

            await foreach (var documentId in _queue.DequeueAllAsync(stoppingToken))
            {
                try
                {
                    // DbContext is scoped, so each document gets a fresh one.
                    using var scope = _scopes.CreateScope();
                    var indexer = scope.ServiceProvider.GetRequiredService<IDocumentIndexer>();
                    await indexer.IndexAsync(documentId, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // One bad document must not stop the worker for every later upload.
                    _log.LogError(ex, "Indexing worker failed on document {Id}", documentId);
                }
            }
        }

        /// <summary>
        /// The queue is in memory, so anything uploaded but not finished when the API
        /// last stopped is only recoverable from the rows themselves.
        /// </summary>
        private async Task RequeueUnfinishedAsync(CancellationToken ct)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ClinicalDbContext>();
                var unfinished = await db.Documents
                    .Where(d => d.Status == ProcessingStatus.Pending || d.Status == ProcessingStatus.Processing)
                    .OrderBy(d => d.UploadedAt)
                    .Select(d => d.Id)
                    .ToListAsync(ct);

                foreach (var id in unfinished) _queue.Enqueue(id);
                if (unfinished.Count > 0)
                    _log.LogInformation("Re-queued {Count} document(s) left unfinished by the last run", unfinished.Count);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // An unhandled exception here would stop the whole host; new uploads can
                // still be indexed without the backlog.
                _log.LogError(ex, "Could not re-queue unfinished documents");
            }
        }
    }
}
