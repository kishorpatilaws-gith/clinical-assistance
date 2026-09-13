using System.Text.RegularExpressions;

namespace ClinicalAI.Api.Application.Services
{
    public interface IPiiRedactionService
    {
        string RedactFreeText(string? text);
    }

    public class PiiRedactionService : IPiiRedactionService
    {
        // Basic pattern-based redaction for free-text fields (Symptoms, MedicalHistory, etc.)
        // where a doctor might have typed something identifying by accident.
        private static readonly Regex EmailPattern = new(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled);
        private static readonly Regex PhonePattern = new(@"\b\d{3}[-.\s]?\d{3}[-.\s]?\d{4}\b", RegexOptions.Compiled);
        private static readonly Regex SsnPattern = new(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.Compiled);

        public string RedactFreeText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            var redacted = text;
            redacted = EmailPattern.Replace(redacted, "[REDACTED-EMAIL]");
            redacted = PhonePattern.Replace(redacted, "[REDACTED-PHONE]");
            redacted = SsnPattern.Replace(redacted, "[REDACTED-SSN]");
            return redacted;
        }
    }
}
