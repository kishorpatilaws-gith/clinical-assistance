using ClinicalAI.Api.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClinicalAI.Api.Controllers
{
    // Part 1 connectivity-test endpoint: a raw passthrough to Bedrock with no
    // redaction applied to the prompt. Locked to the doctor role so it cannot be
    // used as an open proxy against the account's Bedrock quota, and every call
    // is audited.
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Roles = "doctor")]
    public class BedrockController : ControllerBase
    {
        private readonly IBedrockService _bedrockService;
        private readonly IAuditService _audit;

        public BedrockController(IBedrockService bedrockService, IAuditService audit)
        {
            _bedrockService = bedrockService;
            _audit = audit;
        }

        private string CurrentUserEmail =>
            User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
            ?? User.FindFirst("email")?.Value
            ?? "unknown";

        public record PromptRequest(string Prompt);

        [HttpPost("invoke")]
        public async Task<IActionResult> Invoke([FromBody] PromptRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Prompt))
                return BadRequest("Prompt is required.");

            var result = await _bedrockService.InvokeModelAsync(request.Prompt);
            await _audit.LogAsync(CurrentUserEmail, "InvokeBedrock",
                details: $"Prompt length: {request.Prompt.Length} chars");
            return Ok(new { response = result });
        }
    }
}
