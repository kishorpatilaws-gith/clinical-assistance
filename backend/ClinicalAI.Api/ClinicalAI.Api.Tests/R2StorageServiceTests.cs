using Amazon.Runtime;
using Amazon.S3;
using ClinicalAI.Api.Application.Services;
using ClinicalAI.Api.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;

namespace ClinicalAI.Api.Tests;

/// <summary>Pure tests for the key scheme - no network.</summary>
public class R2KeyTests
{
    [Fact]
    public void BuildKey_UsesDocumentTypeAndDatePrefix()
    {
        var key = R2StorageService.BuildKey(DocumentType.ClinicalGuideline, "guide.pdf");

        Assert.StartsWith($"ClinicalGuideline/{DateTime.UtcNow:yyyy-MM-dd}/", key);
        Assert.EndsWith(".pdf", key);
    }

    [Fact]
    public void BuildKey_IsUniquePerCall()
    {
        var a = R2StorageService.BuildKey(DocumentType.Report, "a.pdf");
        var b = R2StorageService.BuildKey(DocumentType.Report, "a.pdf");

        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("patient@example.com-notes.pdf")]
    [InlineData("Jordan Rivera discharge.docx")]
    public void BuildKey_NeverLeaksTheOriginalFilename(string fileName)
    {
        // Keys surface in logs, metrics and error messages. The filename can contain
        // an email or a patient name, so it must not travel in the path.
        var key = R2StorageService.BuildKey(DocumentType.Report, fileName);

        Assert.DoesNotContain("@", key);
        Assert.DoesNotContain(" ", key);
        Assert.DoesNotContain("Rivera", key);
    }

    [Fact]
    public void BuildKey_DropsImplausibleExtensions()
    {
        var key = R2StorageService.BuildKey(DocumentType.Report, "report.thisisnotanextension");

        Assert.EndsWith("/" + key.Split('/').Last(), key);
        Assert.DoesNotContain("thisisnotanextension", key);
    }
}

/// <summary>
/// Round-trips a real object through R2. Skipped automatically when R2 credentials
/// are not configured, so the suite still passes on a machine without them.
/// </summary>
public class R2StorageIntegrationTests
{
    private static IConfiguration Config() =>
        new ConfigurationBuilder().AddUserSecrets<R2StorageIntegrationTests>().AddEnvironmentVariables().Build();

    private static bool Configured(IConfiguration c) =>
        !string.IsNullOrWhiteSpace(c["R2:AccountId"]) &&
        !string.IsNullOrWhiteSpace(c["R2:AccessKey"]) &&
        !string.IsNullOrWhiteSpace(c["R2:SecretKey"]) &&
        !string.IsNullOrWhiteSpace(c["R2:Bucket"]);

    private static IAmazonS3 BuildClient(IConfiguration c) =>
        new AmazonS3Client(c["R2:AccessKey"], c["R2:SecretKey"], new AmazonS3Config
        {
            ServiceURL = $"https://{c["R2:AccountId"]}.r2.cloudflarestorage.com",
            ForcePathStyle = true,
            AuthenticationRegion = "auto",
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        });

    /// <summary>
    /// xunit 2.x has no runtime skip, so the decision is made when the attribute is
    /// constructed: no credentials means the test reports as skipped, not as a
    /// silent pass.
    /// </summary>
    public sealed class RequiresR2Attribute : FactAttribute
    {
        public RequiresR2Attribute()
        {
            if (!Configured(Config()))
                Skip = "R2 credentials are not configured; set them with dotnet user-secrets.";
        }
    }

    [RequiresR2]
    public async Task Upload_Download_Delete_RoundTripsIdenticalBytes()
    {
        var config = Config();
        var sut = new R2StorageService(BuildClient(config), config, NullLogger<R2StorageService>.Instance);

        var payload = Encoding.UTF8.GetBytes($"synthetic round-trip probe {Guid.NewGuid()}");
        string? key = null;

        try
        {
            key = await sut.UploadAsync(new MemoryStream(payload), "probe.txt", "text/plain", DocumentType.Report);
            Assert.StartsWith("Report/", key);

            await using var got = await sut.DownloadAsync(key);
            using var buffer = new MemoryStream();
            await got.CopyToAsync(buffer);

            Assert.Equal(payload, buffer.ToArray());
        }
        finally
        {
            if (key is not null) await sut.DeleteAsync(key);
        }
    }
}
