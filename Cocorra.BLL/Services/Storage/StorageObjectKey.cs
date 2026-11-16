using System;

namespace Cocorra.BLL.Services.Storage
{
    /// <summary>
    /// Turns a stored file reference into the object key inside the bucket. Two forms exist in
    /// the database: legacy full URLs ({PublicUrl}/{bucket}/{key}, path-style) written before
    /// private files stored keys, and bare keys ("Uploads/Voices/&lt;guid&gt;.m4a").
    /// </summary>
    public static class StorageObjectKey
    {
        /// <summary>
        /// The object key, or null when there is nothing to address: empty, an upload error
        /// marker ("Error:..."), or an absolute URL that does not point into <paramref name="bucket"/>.
        /// </summary>
        public static string? From(string? stored, string bucket)
        {
            if (string.IsNullOrWhiteSpace(stored)) return null;

            var value = stored.Trim();
            if (value.StartsWith("Error", StringComparison.OrdinalIgnoreCase)) return null;

            if (IsAbsoluteUrl(value))
            {
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;

                var prefix = $"/{bucket}/";
                var path = Uri.UnescapeDataString(uri.AbsolutePath);
                if (!path.StartsWith(prefix, StringComparison.Ordinal)) return null;

                var key = path[prefix.Length..];
                return key.Length == 0 ? null : key;
            }

            var bare = value.Replace('\\', '/').TrimStart('/');
            return bare.Length == 0 ? null : bare;
        }

        public static bool IsAbsoluteUrl(string value) =>
            value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }
}
