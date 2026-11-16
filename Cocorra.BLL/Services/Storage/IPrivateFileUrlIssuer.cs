using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Cocorra.BLL.Services.Storage
{
    public enum PrivateFileKind
    {
        Voice,
        ReportScreenshot,
        TicketScreenshot
    }

    /// <param name="SubjectId">The user (voice), report or ticket the file belongs to.</param>
    /// <param name="Stored">The stored value: a legacy full URL or a bare object key.</param>
    /// <param name="TargetUserId">The user whose file it is, recorded as the audit target.</param>
    public sealed record PrivateFileRef(Guid SubjectId, string? Stored, Guid? TargetUserId);

    public sealed record IssuedFileUrl(string Url, DateTime ExpiresAtUtc);

    /// <summary>
    /// The only way presigned URLs for private files leave the API: signs them and records who
    /// was given which files in the audit log.
    /// </summary>
    public interface IPrivateFileUrlIssuer
    {
        /// <summary>
        /// Whether a stored value names a file that could be issued. Signs nothing and writes no
        /// audit entry: list responses use it for HasVoice / HasScreenshot.
        /// </summary>
        bool HasFile(string? stored);

        /// <summary>
        /// One result per input, in order (null where the row has no file). Writes a single audit
        /// entry covering every URL issued, or none when nothing was issued.
        /// </summary>
        /// <param name="source">The endpoint that asked, for the audit details (e.g. "Admin.GetUsers").</param>
        Task<IReadOnlyList<IssuedFileUrl?>> IssueAsync(PrivateFileKind kind, IReadOnlyList<PrivateFileRef> files, string source);
    }
}
