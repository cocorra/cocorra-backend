using System;

namespace Cocorra.BLL.Services.Storage
{
    /// <summary>
    /// Issues short-lived presigned GET URLs for private objects (voice recordings, support and
    /// report screenshots). Signing is local: no request reaches storage.
    /// </summary>
    public interface IStorageUrlSigner
    {
        /// <summary>15 minutes.</summary>
        TimeSpan DefaultTtl { get; }

        /// <summary>The object key behind a stored value (legacy full URL or bare key), or null.</summary>
        string? GetObjectKey(string? stored);

        /// <summary>
        /// A presigned GET URL on the public storage host, valid for <paramref name="ttl"/>
        /// (default <see cref="DefaultTtl"/>), or null when <paramref name="stored"/> names no object.
        /// </summary>
        string? GetReadUrl(string? stored, TimeSpan? ttl = null);
    }
}
