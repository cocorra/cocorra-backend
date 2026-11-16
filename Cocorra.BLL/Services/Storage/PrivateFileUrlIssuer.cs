using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Cocorra.BLL.Services.AuditLogs;
using Cocorra.DAL.Models;
using Microsoft.AspNetCore.Http;

namespace Cocorra.BLL.Services.Storage
{
    public class PrivateFileUrlIssuer : IPrivateFileUrlIssuer
    {
        private readonly IStorageUrlSigner _signer;
        private readonly IAuditLogService _auditLog;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public PrivateFileUrlIssuer(
            IStorageUrlSigner signer,
            IAuditLogService auditLog,
            IHttpContextAccessor httpContextAccessor)
        {
            _signer = signer;
            _auditLog = auditLog;
            _httpContextAccessor = httpContextAccessor;
        }

        public async Task<IReadOnlyList<IssuedFileUrl?>> IssueAsync(
            PrivateFileKind kind, IReadOnlyList<PrivateFileRef> files, string source)
        {
            var expiresAtUtc = DateTime.UtcNow.Add(_signer.DefaultTtl);
            var results = new IssuedFileUrl?[files.Count];
            var issued = new List<(PrivateFileRef File, string Key)>();

            for (var i = 0; i < files.Count; i++)
            {
                var key = _signer.GetObjectKey(files[i].Stored);
                var url = key is null ? null : _signer.GetReadUrl(key);
                if (url is null) continue;

                results[i] = new IssuedFileUrl(url, expiresAtUtc);
                issued.Add((files[i], key!));
            }

            if (issued.Count > 0)
            {
                // One row per request rather than per URL: a user-list page issues one URL per
                // user, and the row still names every file and the user it belongs to.
                var subjectType = SubjectType(kind);
                await _auditLog.RecordAsync(new AuditEntry(
                    kind == PrivateFileKind.Voice ? AuditActions.VoiceUrlIssued : AuditActions.ScreenshotUrlIssued,
                    CurrentUserId(),
                    issued.Count == 1 ? issued[0].File.TargetUserId : null,
                    issued.Count == 1 ? $"{subjectType} {issued[0].File.SubjectId}" : $"{issued.Count} {subjectType}s",
                    new
                    {
                        source,
                        kind = kind.ToString(),
                        expiresAtUtc,
                        files = issued.Select(x => new
                        {
                            subjectType,
                            subjectId = x.File.SubjectId,
                            userId = x.File.TargetUserId,
                            objectKey = x.Key
                        })
                    }));
            }

            return results;
        }

        private static string SubjectType(PrivateFileKind kind) => kind switch
        {
            PrivateFileKind.Voice => "user",
            PrivateFileKind.ReportScreenshot => "report",
            PrivateFileKind.TicketScreenshot => "ticket",
            _ => "file"
        };

        private Guid? CurrentUserId() =>
            Guid.TryParse(_httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
                ? id
                : null;
    }
}
