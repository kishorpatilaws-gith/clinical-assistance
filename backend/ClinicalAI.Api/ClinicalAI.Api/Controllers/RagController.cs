using ClinicalAI.Api.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClinicalAI.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Roles = "doctor")]
    public class RagController : ControllerBase
    {
        private readonly IRagService _rag;
        private readonly IAuditService _audit;
        private readonly ILogger<RagController> _log;

        public RagController(IRagService rag, IAuditService audit, ILogger<RagController> log)
        {
            _rag = rag;
            _audit = audit;
            _log = log;
        }

        private string CurrentUserEmail =>
            User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
            ?? User.FindFirst("email")?.Value
            ?? "unknown";

        public record AskRequest(string Question, int? TopK, Guid? DocumentId);

        /// <summary>
        /// Ask a question of the indexed clinical documents. The answer is generated
        /// strictly from retrieved passages and carries citations back to them.
        /// </summary>
        [HttpPost("ask")]
        public async Task<IActionResult> Ask([FromBody] AskRequest request, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.Question))
                return BadRequest(new { error = "A question is required." });

            if (request.Question.Length > 2000)
                return BadRequest(new { error = "Question is too long (2000 characters maximum)." });

            // 5 by default: retrieval measurably does not put the answering passage
            // first every time, so the model is given several to choose between.
            var topK = Math.Clamp(request.TopK ?? 5, 1, 20);

            try
            {
                var result = await _rag.AskAsync(request.Question, topK, request.DocumentId, ct);

                await _audit.LogAsync(CurrentUserEmail, "AskDocuments",
                    details: $"TopK: {topK}, Sources: {result.Sources.Count}, Model: {result.Model}");

                return Ok(new
                {
                    result.Question,
                    result.Answer,
                    result.Model,
                    sources = result.Sources,
                    // Returned so a clinician can see exactly what the model was given -
                    // an answer is only as trustworthy as the passages behind it.
                    retrieved = result.Retrieved.Select(h => new
                    {
                        h.Score,
                        h.FileName,
                        h.ChunkIndex,
                        h.Text,
                        h.CharStart,
                        h.CharEnd
                    })
                });
            }
            catch (DocumentProcessingException ex)
            {
                _log.LogWarning(ex, "Retrieval failed for question");
                return StatusCode(503, new { error = ex.Message });
            }
        }
    }
}
