using ClinicalAI.Api.Application.Services;
using ClinicalAI.Api.Domain.Entities;
using ClinicalAI.Api.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClinicalAI.Api.Tests;

/// <summary>
/// The background indexing step, against an in-memory database and fake R2 and
/// processor. Indexing now happens after the upload request has returned, so the
/// row's status is the only thing a clinician ever sees of it - every outcome has
/// to land there.
/// </summary>
public class DocumentIndexerTests
{
    private readonly DbContextOptions<ClinicalDbContext> _options =
        new DbContextOptionsBuilder<ClinicalDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    private readonly FakeStorage _storage = new();
    private readonly FakeProcessor _processor = new();

    private async Task<Guid> SeedAsync()
    {
        await using var db = new ClinicalDbContext(_options);
        var doc = new Document
        {
            FileName = "book.pdf",
            R2Key = "documents/book.pdf",
            ContentType = "application/pdf",
            DocumentType = DocumentType.Report,
            Status = ProcessingStatus.Pending
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        return doc.Id;
    }

    private async Task IndexAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = new ClinicalDbContext(_options);
        var indexer = new DocumentIndexer(db, _storage, _processor, NullLogger<DocumentIndexer>.Instance);
        await indexer.IndexAsync(id, ct);
    }

    private async Task<Document> ReloadAsync(Guid id)
    {
        await using var db = new ClinicalDbContext(_options);
        return await db.Documents.SingleAsync(d => d.Id == id);
    }

    [Fact]
    public async Task Success_MarksTheRowCompletedWithTheExtractedText()
    {
        var id = await SeedAsync();

        await IndexAsync(id);

        var doc = await ReloadAsync(id);
        Assert.Equal(ProcessingStatus.Completed, doc.Status);
        Assert.Equal(FakeProcessor.ExtractedText, doc.ExtractedText);
        Assert.NotNull(doc.ProcessedAt);
    }

    [Fact]
    public async Task PassesTheRowIdThroughAsTheVectorDocumentId()
    {
        // The vector store and the relational row must share an id, or a re-index
        // duplicates chunks instead of overwriting them.
        var id = await SeedAsync();

        await IndexAsync(id);

        Assert.Equal([id], _processor.Indexed);
    }

    [Fact]
    public async Task ProcessorFailure_MarksTheRowFailed()
    {
        var id = await SeedAsync();
        _processor.IndexFailure = new DocumentProcessingException("The document processor timed out after 3,600s.");

        await IndexAsync(id);

        var doc = await ReloadAsync(id);
        Assert.Equal(ProcessingStatus.Failed, doc.Status);
        Assert.Null(doc.ExtractedText);
        Assert.NotNull(doc.ProcessedAt);
    }

    [Fact]
    public async Task StorageFailure_MarksTheRowFailed()
    {
        // The original is read back from R2, so R2 being unreachable is a failure of
        // this document - not something that may leave it Processing for ever.
        var id = await SeedAsync();
        _storage.DownloadFailure = new IOException("R2 is unreachable");

        await IndexAsync(id);

        Assert.Equal(ProcessingStatus.Failed, (await ReloadAsync(id)).Status);
        Assert.Empty(_processor.Indexed);
    }

    [Fact]
    public async Task MissingRow_IsSkippedWithoutCallingTheProcessor()
    {
        await IndexAsync(Guid.NewGuid());

        Assert.Empty(_processor.Indexed);
    }

    [Fact]
    public async Task RowDeletedWhileIndexing_ClearsTheVectorsJustWritten()
    {
        var id = await SeedAsync();
        _processor.DuringIndex = async () =>
        {
            await using var db = new ClinicalDbContext(_options);
            db.Documents.Remove(await db.Documents.SingleAsync(d => d.Id == id));
            await db.SaveChangesAsync();
        };

        await IndexAsync(id);

        Assert.Equal([id], _processor.Deleted);
    }

    [Fact]
    public async Task Shutdown_LeavesTheRowProcessingSoTheNextStartRequeuesIt()
    {
        var id = await SeedAsync();
        using var shutdown = new CancellationTokenSource();
        _processor.DuringIndex = () =>
        {
            shutdown.Cancel();
            shutdown.Token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => IndexAsync(id, shutdown.Token));

        Assert.Equal(ProcessingStatus.Processing, (await ReloadAsync(id)).Status);
    }

    private sealed class FakeStorage : IR2StorageService
    {
        public Exception? DownloadFailure { get; set; }

        public Task<string> UploadAsync(Stream content, string fileName, string contentType,
                                        DocumentType documentType, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Stream> DownloadAsync(string key, CancellationToken ct = default) =>
            DownloadFailure is null
                ? Task.FromResult<Stream>(new MemoryStream("%PDF-1.7"u8.ToArray()))
                : Task.FromException<Stream>(DownloadFailure);

        public Task DeleteAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeProcessor : IDocumentProcessorClient
    {
        public const string ExtractedText = "Charles's law relates volume to temperature.";

        public Exception? IndexFailure { get; set; }
        public Func<Task>? DuringIndex { get; set; }
        public List<Guid> Indexed { get; } = [];
        public List<Guid> Deleted { get; } = [];

        public Task<ExtractionResult> ExtractTextAsync(Stream content, string fileName, string contentType,
                                                       CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async Task<IndexResult> IndexAsync(Stream content, string fileName, string contentType,
                                                  Guid documentId, string documentType,
                                                  CancellationToken ct = default)
        {
            Indexed.Add(documentId);
            if (DuringIndex is not null) await DuringIndex();
            if (IndexFailure is not null) throw IndexFailure;
            return new IndexResult(documentId.ToString(), ChunksIndexed: 1, CharCount: ExtractedText.Length,
                                   PageCount: 1, DetectedType: "pdf", Text: ExtractedText);
        }

        public Task<SearchResponse> SearchAsync(string query, int limit = 5, Guid? documentId = null,
                                                CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteIndexAsync(Guid documentId, CancellationToken ct = default)
        {
            Deleted.Add(documentId);
            return Task.CompletedTask;
        }
    }
}
