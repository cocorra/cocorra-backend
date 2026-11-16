using System;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Cocorra.BLL.Services.UploadService;
using Microsoft.Extensions.Options;

namespace Cocorra.BLL.Services.Storage
{
    public class StorageUrlSigner : IStorageUrlSigner, IDisposable
    {
        // A SigV4 signature covers the Host header, so the URL must be signed for the host the
        // browser will request (the public storage domain), not the internal http://minio:9000
        // the upload client talks to. This client is never used to send a request; the reverse
        // proxy in front of MinIO must forward Host unchanged for the signature to verify.
        private readonly AmazonS3Client _signingClient;
        private readonly string _bucket;
        private readonly bool _useHttp;

        public StorageUrlSigner(IOptions<MinioSettings> options)
        {
            var settings = options.Value;
            _bucket = settings.BucketName;

            var publicUrl = (settings.PublicUrl ?? string.Empty).Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(publicUrl))
                throw new InvalidOperationException("Minio:PublicUrl is required to sign storage URLs.");
            _useHttp = publicUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

            _signingClient = new AmazonS3Client(
                new BasicAWSCredentials(settings.AccessKey, settings.SecretKey),
                new AmazonS3Config
                {
                    ServiceURL = publicUrl,
                    ForcePathStyle = true,
                    UseHttp = _useHttp,
                    // MinIO's default region; the upload client resolves to the same value.
                    AuthenticationRegion = "us-east-1"
                });
        }

        public TimeSpan DefaultTtl { get; } = TimeSpan.FromMinutes(15);

        public string? GetObjectKey(string? stored) => StorageObjectKey.From(stored, _bucket);

        public string? GetReadUrl(string? stored, TimeSpan? ttl = null)
        {
            var key = GetObjectKey(stored);
            if (key is null) return null;

            return _signingClient.GetPreSignedURL(new GetPreSignedUrlRequest
            {
                BucketName = _bucket,
                Key = key,
                Verb = HttpVerb.GET,
                Protocol = _useHttp ? Protocol.HTTP : Protocol.HTTPS,
                Expires = DateTime.UtcNow.Add(ttl ?? DefaultTtl)
            });
        }

        public void Dispose() => _signingClient.Dispose();
    }
}
