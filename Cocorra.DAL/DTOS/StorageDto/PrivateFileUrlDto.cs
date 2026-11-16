using System;

namespace Cocorra.DAL.DTOS.StorageDto
{
    /// <summary>A short-lived presigned URL to a private file. Request a new one once it expires.</summary>
    public class PrivateFileUrlDto
    {
        public string Url { get; set; } = string.Empty;
        public DateTime ExpiresAtUtc { get; set; }
    }
}
