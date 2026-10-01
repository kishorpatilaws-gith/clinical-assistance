using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClinicalAI.Api.Application.Services
{
    public record ExtractionResult(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("page_count")] int PageCount,
        [property: JsonPropertyName("char_count")] int CharCount,
        [property: JsonPropertyName("detected_type")] string DetectedType);

    public record IndexResult(
        [property: JsonPropertyName("document_id")] string DocumentId,
        [property: JsonPropertyName("chunks_indexed")] int ChunksIndexed,
        [property: JsonPropertyName("char_count")] int CharCount,
        [property: JsonPropertyName("page_count")] int PageCount,
        [property: JsonPropertyName("detected_type")] string DetectedType,
        [property: JsonPropertyName("text")] string Text);

    public record SearchHit(
        [property: JsonPropertyName("score")] double Score,
        [property: JsonPropertyName("document_id")] string DocumentId,
        [property: JsonPropertyName("file_name")] string FileName,
        [property: JsonPropertyName("document_type")] string DocumentType,
        [property: JsonPropertyName("chunk_index")] int ChunkIndex,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("char_start")] int CharStart,
        [property: JsonPropertyName("char_end")] int CharEnd);

    public record SearchResponse(
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("hits")] List<SearchHit> Hits,
        [property: JsonPropertyName("model")] string Model);

    public interface IDocumentProcessorClient
    {
        Task<ExtractionResult> ExtractTextAsync(Stream content, string fileName, string contentType,
                                                CancellationToken ct = default);

        /// <summary>Extract, chunk, embed and store in the vector index.</summary>
        Task<IndexResult> IndexAsync(Stream content, string fileName, string contentType,
                                     Guid documentId, string documentType,
                                     CancellationToken ct = default);

        Task<SearchResponse> SearchAsync(string query, int limit = 5, Guid? documentId = null,
                                         CancellationToken ct = default);

        Task DeleteIndexAsync(Guid documentId, CancellationToken ct = default);
    }

    /// <summary>
    /// Talks to the Python document-processor over HTTP. It is a separate process
    /// with its own lifecycle, so every failure mode here is a network failure mode:
    /// it may be down, slow, or return a structured error. None of those should take
    /// the API down with it.
    /// </summary>
    public class DocumentProcessorClient : IDocumentProcessorClient
    {
        private readonly HttpClient _http;
        private readonly ILogger<DocumentProcessorClient> _log;

        // Per call rather than HttpClient.Timeout: indexing a whole book legitimately
        // takes many minutes in the background, while a search sits inside a clinician's
        // request and should fail fast.
        private readonly TimeSpan _timeout;
        private readonly TimeSpan _indexTimeout;

        public DocumentProcessorClient(HttpClient http, IConfiguration config, ILogger<DocumentProcessorClient> log)
        {
            _http = http;
            _log = log;
            var cfg = config.GetSection("DocumentProcessor");
            _timeout = TimeSpan.FromSeconds(cfg.GetValue("TimeoutSeconds", 120));
            _indexTimeout = TimeSpan.FromSeconds(cfg.GetValue("IndexTimeoutSeconds", 3600));
        }

        public async Task<ExtractionResult> ExtractTextAsync(Stream content, string fileName,
                                                             string contentType, CancellationToken ct = default)
        {
            using var form = BuildFileForm(content, fileName, contentType);
            var body = await SendAsync(HttpMethod.Post, "/extract-text", form, fileName, _timeout, ct);
            return Deserialize<ExtractionResult>(body);
        }

        public async Task<IndexResult> IndexAsync(Stream content, string fileName, string contentType,
                                                  Guid documentId, string documentType,
                                                  CancellationToken ct = default)
        {
            using var form = BuildFileForm(content, fileName, contentType);
            form.Add(new StringContent(documentId.ToString()), "document_id");
            form.Add(new StringContent(documentType), "document_type");

            var body = await SendAsync(HttpMethod.Post, "/index", form, fileName, _indexTimeout, ct);
            return Deserialize<IndexResult>(body);
        }

        public async Task<SearchResponse> SearchAsync(string query, int limit = 5,
                                                      Guid? documentId = null, CancellationToken ct = default)
        {
            var payload = JsonContent.Create(new
            {
                query,
                limit,
                document_id = documentId?.ToString()
            });

            var body = await SendAsync(HttpMethod.Post, "/search", payload, "search", _timeout, ct);
            return Deserialize<SearchResponse>(body);
        }

        public async Task DeleteIndexAsync(Guid documentId, CancellationToken ct = default)
        {
            await SendAsync(HttpMethod.Delete, $"/index/{documentId}", null, documentId.ToString(), _timeout, ct);
        }

        private static MultipartFormDataContent BuildFileForm(Stream content, string fileName, string contentType)
        {
            var form = new MultipartFormDataContent();
            var filePart = new StreamContent(content);
            filePart.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(filePart, "file", fileName);
            return form;
        }

        private async Task<string> SendAsync(HttpMethod method, string path, HttpContent? content,
                                             string label, TimeSpan timeout, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            HttpResponseMessage response;
            string body;
            try
            {
                response = await _http.SendAsync(request, timeoutCts.Token);
                body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            }
            catch (HttpRequestException ex)
            {
                throw new DocumentProcessingException(
                    "The document processor is unreachable. Is it running on port 8000?", ex);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new DocumentProcessingException(
                    $"The document processor timed out after {timeout.TotalSeconds:N0}s.", ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                // FastAPI reports problems as {"detail": "..."} - surface that rather
                // than a bare status code, so the clinician sees why it failed.
                var detail = TryReadDetail(body) ?? $"HTTP {(int)response.StatusCode}";
                _log.LogWarning("{Path} failed for {Label}: {Detail}", path, label, detail);
                throw new DocumentProcessingException(detail);
            }

            return body;
        }

        private static T Deserialize<T>(string body) =>
            JsonSerializer.Deserialize<T>(body)
            ?? throw new DocumentProcessingException("The document processor returned an empty response.");

        private static string? TryReadDetail(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.TryGetProperty("detail", out var d) ? d.ToString() : null;
            }
            catch (JsonException) { return null; }
        }
    }

    public class DocumentProcessingException : Exception
    {
        public DocumentProcessingException(string message, Exception? inner = null) : base(message, inner) { }
    }
}
