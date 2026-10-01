using ClinicalAI.Api.Application.Services;

namespace ClinicalAI.Api.Tests;

/// <summary>
/// Prompt assembly and citation mapping. These run without Bedrock, Qdrant or a
/// network: the prompt is where answer trustworthiness is actually decided, so it
/// is worth pinning independently of whether the model behaves on any given day.
/// </summary>
public class RagPromptTests
{
    private static List<SearchHit> Hits(params string[] texts) =>
        texts.Select((t, i) => new SearchHit(
            Score: 0.9 - i * 0.1,
            DocumentId: Guid.NewGuid().ToString(),
            FileName: $"guideline-{i}.pdf",
            DocumentType: "ClinicalGuideline",
            ChunkIndex: i,
            Text: t,
            CharStart: i * 100,
            CharEnd: i * 100 + t.Length)).ToList();

    [Fact]
    public void Prompt_NumbersEveryPassage()
    {
        var prompt = RagService.BuildPrompt("what dose?", Hits("first passage", "second passage"));

        Assert.Contains("[1]", prompt);
        Assert.Contains("[2]", prompt);
        Assert.Contains("first passage", prompt);
        Assert.Contains("second passage", prompt);
    }

    [Fact]
    public void Prompt_NamesTheSourceFileForEachPassage()
    {
        // Without the file name in context the model cannot attribute a claim, and
        // the citation markers have nothing to resolve against.
        var prompt = RagService.BuildPrompt("what dose?", Hits("passage"));
        Assert.Contains("guideline-0.pdf", prompt);
    }

    [Fact]
    public void Prompt_RestrictsTheModelToTheSuppliedPassages()
    {
        var prompt = RagService.BuildPrompt("what dose?", Hits("passage"));
        Assert.Contains("ONLY from the numbered passages", prompt);
        Assert.Contains("Do not add clinical facts", prompt);
    }

    [Fact]
    public void Prompt_CarriesTheExactRefusalPhrase()
    {
        // The refusal is asserted against in the live test, so the wording the model
        // is asked for has to match the constant the API reports.
        var prompt = RagService.BuildPrompt("what dose?", Hits("passage"));
        Assert.Contains(RagService.NoAnswerPhrase, prompt);
    }

    [Fact]
    public void Prompt_KeepsThePartTwoSafetyBoundary()
    {
        var prompt = RagService.BuildPrompt("what dose?", Hits("passage"));
        Assert.Contains("Do not state a diagnosis", prompt);
        Assert.Contains("considerations", prompt);
    }

    [Fact]
    public void Prompt_EndsWithTheQuestion()
    {
        var prompt = RagService.BuildPrompt("what is the starting dose?", Hits("passage"));
        Assert.Contains("Question: what is the starting dose?", prompt);
    }

    [Fact]
    public void Citations_MarkersAreOneBasedAndMatchPassageOrder()
    {
        var hits = Hits("a", "b", "c");
        var citations = RagService.BuildCitations(hits);

        Assert.Equal(new[] { 1, 2, 3 }, citations.Select(c => c.Marker));
        Assert.Equal(hits.Select(h => h.FileName), citations.Select(c => c.FileName));
    }

    [Fact]
    public void Citations_CarryOffsetsSoASourcePassageCanBeLocated()
    {
        var citation = RagService.BuildCitations(Hits("passage text"))[0];

        Assert.Equal(0, citation.CharStart);
        Assert.Equal("passage text".Length, citation.CharEnd);
        Assert.Equal(0, citation.ChunkIndex);
        Assert.NotEqual(Guid.Empty, citation.DocumentId);
    }

    [Fact]
    public void Citations_TolerateAMalformedDocumentId()
    {
        var hit = new SearchHit(0.5, "not-a-guid", "f.pdf", "Report", 0, "text", 0, 4);
        Assert.Equal(Guid.Empty, RagService.BuildCitations([hit])[0].DocumentId);
    }
}
