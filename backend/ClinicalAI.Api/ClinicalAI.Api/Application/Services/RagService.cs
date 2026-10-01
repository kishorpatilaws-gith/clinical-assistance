using System.Text;

namespace ClinicalAI.Api.Application.Services
{
    public record Citation(int Marker, string FileName, string DocumentType,
                           Guid DocumentId, int ChunkIndex, int CharStart, int CharEnd, double Score);

    public record RagAnswer(string Question, string Answer, List<Citation> Sources,
                            List<SearchHit> Retrieved, string Model);

    public interface IRagService
    {
        Task<RagAnswer> AskAsync(string question, int topK = 5, Guid? documentId = null,
                                 CancellationToken ct = default);
    }

    /// <summary>
    /// Retrieval-augmented generation over the indexed clinical documents.
    ///
    /// Orchestrated here rather than in the Python service on purpose: generating
    /// clinical text has to sit behind the same authorization, audit and redaction
    /// the rest of the API enforces, and Bedrock is already wired up on this side.
    /// Python stays a focused extraction and retrieval service.
    /// </summary>
    public class RagService : IRagService
    {
        private readonly IDocumentProcessorClient _processor;
        private readonly IBedrockService _bedrock;
        private readonly IPiiRedactionService _redaction;
        private readonly ILogger<RagService> _log;

        public RagService(IDocumentProcessorClient processor, IBedrockService bedrock,
                          IPiiRedactionService redaction, ILogger<RagService> log)
        {
            _processor = processor;
            _bedrock = bedrock;
            _redaction = redaction;
            _log = log;
        }

        /// <summary>
        /// Said by the model when the retrieved passages do not answer the question.
        /// Kept as a constant so the phrasing is testable rather than incidental.
        /// </summary>
        public const string NoAnswerPhrase = "The provided documents do not address this question.";

        public async Task<RagAnswer> AskAsync(string question, int topK = 5, Guid? documentId = null,
                                              CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(question))
                throw new ArgumentException("Question must not be empty.", nameof(question));

            // The clinician's own question can contain identifiers they typed by habit.
            var safeQuestion = _redaction.RedactFreeText(question);

            // Retrieve generously. Measured in 5.3: the top-scoring chunk is not
            // reliably the one that answers the question - a short, topically central
            // chunk outranks the specific one - and absolute scores are not comparable
            // between questions, so a score threshold would discard good passages.
            // Give the model several and let it choose.
            var search = await _processor.SearchAsync(safeQuestion, topK, documentId, ct);

            if (search.Hits.Count == 0)
            {
                return new RagAnswer(question, NoAnswerPhrase, [], [], _bedrock.ModelId);
            }

            var prompt = BuildPrompt(safeQuestion, search.Hits);
            var answer = await _bedrock.InvokeModelAsync(prompt);

            _log.LogInformation("RAG answered in {Chars} chars from {Hits} passage(s)",
                answer.Length, search.Hits.Count);

            return new RagAnswer(
                Question: question,
                Answer: answer.Trim(),
                Sources: BuildCitations(search.Hits),
                Retrieved: search.Hits,
                Model: _bedrock.ModelId);
        }

        internal static List<Citation> BuildCitations(List<SearchHit> hits) =>
            hits.Select((h, i) => new Citation(
                    Marker: i + 1,
                    FileName: h.FileName,
                    DocumentType: h.DocumentType,
                    DocumentId: Guid.TryParse(h.DocumentId, out var id) ? id : Guid.Empty,
                    ChunkIndex: h.ChunkIndex,
                    CharStart: h.CharStart,
                    CharEnd: h.CharEnd,
                    Score: h.Score))
                .ToList();

        /// <summary>
        /// Three rules do the real work here, each guarding a specific failure:
        ///
        /// 1. Answer only from the passages. Without it the model answers from its
        ///    training data and produces confident, uncited clinical claims that
        ///    cannot be checked against any source.
        /// 2. Say so when the passages do not answer. A model asked to be helpful
        ///    will otherwise fill the gap with something plausible.
        /// 3. Considerations, never diagnosis - carried over from Part 2, and the
        ///    project's stated safety boundary.
        /// </summary>
        internal static string BuildPrompt(string question, List<SearchHit> hits)
        {
            var context = new StringBuilder();
            for (var i = 0; i < hits.Count; i++)
            {
                context.AppendLine($"[{i + 1}] (source: {hits[i].FileName})");
                context.AppendLine(hits[i].Text.Trim());
                context.AppendLine();
            }

            return $"""
            You are a clinical decision-support assistant helping a licensed clinician
            search their own document library. Answer ONLY from the numbered passages
            below.

            Rules:
            - Use only information present in the passages. Do not add clinical facts
              from your own knowledge, however confident you are.
            - Cite every claim with its passage number in square brackets, like [1].
            - If the passages do not contain the answer, reply with exactly:
              "{NoAnswerPhrase}"
              Do not guess, and do not answer from general knowledge.
            - Do not state a diagnosis and do not prescribe. Frame everything as
              considerations for the clinician to evaluate.
            - Be concise. Prefer a short paragraph or a few bullets.

            Passages:
            {context}
            Question: {question}
            """;
        }
    }
}
