namespace ClinicalAI.Api.Domain.Entities
{
    public enum DocumentType { Report, ClinicalGuideline, ResearchPaper }
    public enum ProcessingStatus { Pending, Processing, Completed, Failed }

    public class Document
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string FileName { get; set; } = string.Empty;
        public string R2Key { get; set; } = string.Empty;
        public DocumentType DocumentType { get; set; }
        public string ContentType { get; set; } = string.Empty;
        public ProcessingStatus Status { get; set; } = ProcessingStatus.Pending;
        public string? ExtractedText { get; set; }
        public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ProcessedAt { get; set; }
    }
}
