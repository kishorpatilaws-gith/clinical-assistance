using Amazon.S3;
using Amazon.S3.Model;
using ClinicalAI.Api.Domain.Entities;

namespace ClinicalAI.Api.Application.Services
{
    public interface IR2StorageService
    {
        /// <summary>Stores an object and returns the R2 key it was written to.</summary>
        Task<string> UploadAsync(Stream content, string fileName, string contentType,
                                 DocumentType documentType, CancellationToken ct = default);

        Task<Stream> DownloadAsync(string key, CancellationToken ct = default);

        Task DeleteAsync(string key, CancellationToken ct = default);
    }

    public class R2StorageService : IR2StorageService
    {
        private readonly IAmazonS3 _s3;
        private readonly string _bucket;
        private readonly ILogger<R2StorageService> _log;

        public R2StorageService(IAmazonS3 s3, IConfiguration config, ILogger<R2StorageService> log)
        {
            _s3 = s3;
            _log = log;
            _bucket = config["R2:Bucket"]
                ?? throw new InvalidOperationException(
                    "R2:Bucket is not configured. Set it with: dotnet user-secrets set \"R2:Bucket\" \"<bucket>\"");
        }

        /// <summary>
        /// Keys are {documentType}/{date}/{guid}{ext}. Deliberately not keyed by user
        /// identity: object keys are visible in logs, metrics and error messages, so
        /// putting an email or patient name in the path leaks it everywhere the key
        /// travels. The owning user lives in the Postgres row, not the key.
        /// </summary>
        public static string BuildKey(DocumentType documentType, string fileName)
        {
            var ext = Path.GetExtension(fileName);
            if (ext.Length > 10) ext = string.Empty;   // guard against junk "extensions"
            return $"{documentType}/{DateTime.UtcNow:yyyy-MM-dd}/{Guid.NewGuid():N}{ext.ToLowerInvariant()}";
        }

        public async Task<string> UploadAsync(Stream content, string fileName, string contentType,
                                              DocumentType documentType, CancellationToken ct = default)
        {
            var key = BuildKey(documentType, fileName);

            var request = new PutObjectRequest
            {
                BucketName = _bucket,
                Key = key,
                InputStream = content,
                ContentType = contentType,
                DisablePayloadSigning = true
            };

            // The original filename is metadata, never part of the key - it can contain
            // anything the uploader typed.
            request.Metadata.Add("original-filename", SanitizeForHeader(fileName));

            await _s3.PutObjectAsync(request, ct);
            _log.LogInformation("Uploaded {Key} to R2 bucket {Bucket}", key, _bucket);
            return key;
        }

        public async Task<Stream> DownloadAsync(string key, CancellationToken ct = default)
        {
            var response = await _s3.GetObjectAsync(new GetObjectRequest
            {
                BucketName = _bucket,
                Key = key
            }, ct);

            // Copy out so the caller owns a stream that outlives the response.
            var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, ct);
            buffer.Position = 0;
            return buffer;
        }

        public async Task DeleteAsync(string key, CancellationToken ct = default)
        {
            await _s3.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = _bucket,
                Key = key
            }, ct);
            _log.LogInformation("Deleted {Key} from R2 bucket {Bucket}", key, _bucket);
        }

        /// <summary>HTTP header values must be ASCII; metadata is not worth a 400.</summary>
        private static string SanitizeForHeader(string value)
        {
            var clean = new string(value.Where(c => c is >= (char)32 and < (char)127).ToArray());
            return clean.Length > 200 ? clean[..200] : clean;
        }
    }
}
