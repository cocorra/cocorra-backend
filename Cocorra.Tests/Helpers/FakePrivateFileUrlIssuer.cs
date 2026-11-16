using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.BLL.Services.Storage;

namespace Cocorra.Tests.Helpers;

/// <summary>
/// Stands in for the real issuer in service tests: "signs" each stored value as
/// "signed:&lt;stored&gt;" and remembers every call, so a test can assert both the URL a DTO
/// carries and that issuance went through the audited path.
/// </summary>
public sealed class FakePrivateFileUrlIssuer : IPrivateFileUrlIssuer
{
    public static readonly DateTime ExpiresAtUtc = new(2030, 1, 1, 0, 15, 0, DateTimeKind.Utc);

    public List<(PrivateFileKind Kind, IReadOnlyList<PrivateFileRef> Files, string Source)> Calls { get; } = new();

    // Mirrors the real issuer closely enough for service tests: empty and "Error:..." are no file.
    public bool HasFile(string? stored) =>
        !string.IsNullOrWhiteSpace(stored) && !stored.StartsWith("Error", StringComparison.OrdinalIgnoreCase);

    public Task<IReadOnlyList<IssuedFileUrl?>> IssueAsync(
        PrivateFileKind kind, IReadOnlyList<PrivateFileRef> files, string source)
    {
        Calls.Add((kind, files, source));
        IReadOnlyList<IssuedFileUrl?> results = files
            .Select(f => string.IsNullOrWhiteSpace(f.Stored)
                ? null
                : new IssuedFileUrl($"signed:{f.Stored}", ExpiresAtUtc))
            .ToList();
        return Task.FromResult(results);
    }
}
