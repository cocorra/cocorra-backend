using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;
using Cocorra.BLL.Services.AuditLogs;
using Cocorra.BLL.Services.Storage;
using Cocorra.BLL.Services.UploadService;
using Cocorra.DAL.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class StorageUrlSignerTests
{
    private const string Bucket = "cocorra-assets";

    private static StorageUrlSigner CreateSigner(string publicUrl = "https://storage.cocorraapp.com") =>
        new(Options.Create(new MinioSettings
        {
            // The upload client's internal endpoint must NOT end up in signed URLs.
            Endpoint = "http://minio:9000",
            AccessKey = "test-access-key",
            SecretKey = "test-secret-key",
            BucketName = Bucket,
            PublicUrl = publicUrl
        }));

    private static (Uri Uri, Dictionary<string, string> Query) Parse(string url)
    {
        var uri = new Uri(url);
        var query = HttpUtility.ParseQueryString(uri.Query);
        return (uri, query.AllKeys.ToDictionary(k => k!, k => query[k]!));
    }

    [Fact]
    public void LegacyFullUrl_IsSignedForThePublicHost()
    {
        var url = CreateSigner().GetReadUrl($"https://storage.cocorraapp.com/{Bucket}/Uploads/Voices/a.m4a");

        var (uri, query) = Parse(url!);
        Assert.Equal("https", uri.Scheme);
        Assert.Equal("storage.cocorraapp.com", uri.Host);
        Assert.Equal($"/{Bucket}/Uploads/Voices/a.m4a", uri.AbsolutePath);
        Assert.Equal("AWS4-HMAC-SHA256", query["X-Amz-Algorithm"]);
        Assert.StartsWith("test-access-key/", query["X-Amz-Credential"]);
        Assert.EndsWith("/us-east-1/s3/aws4_request", query["X-Amz-Credential"]);
        Assert.Equal("host", query["X-Amz-SignedHeaders"]);
        Assert.Matches("^[0-9a-f]{64}$", query["X-Amz-Signature"]);
    }

    [Fact]
    public void BareKey_AndLegacyUrl_AddressTheSameObject()
    {
        var signer = CreateSigner();

        var fromKey = Parse(signer.GetReadUrl("Uploads/img/Support/b.png")!).Uri.AbsolutePath;
        var fromSlashedKey = Parse(signer.GetReadUrl("/Uploads/img/Support/b.png")!).Uri.AbsolutePath;
        var fromUrl = Parse(signer.GetReadUrl($"https://old-host.example/{Bucket}/Uploads/img/Support/b.png")!).Uri.AbsolutePath;

        Assert.Equal($"/{Bucket}/Uploads/img/Support/b.png", fromKey);
        Assert.Equal(fromKey, fromSlashedKey);
        Assert.Equal(fromKey, fromUrl);
    }

    [Fact]
    public void DefaultExpiry_IsFifteenMinutes()
    {
        var signer = CreateSigner();

        Assert.Equal(TimeSpan.FromMinutes(15), signer.DefaultTtl);
        var expires = int.Parse(Parse(signer.GetReadUrl("Uploads/Voices/a.m4a")!).Query["X-Amz-Expires"]);
        // Expires is computed from the moment of signing; allow a second of drift.
        Assert.InRange(expires, 899, 900);
    }

    [Fact]
    public void CustomTtl_IsHonoured()
    {
        var expires = int.Parse(Parse(CreateSigner().GetReadUrl("Uploads/Voices/a.m4a", TimeSpan.FromHours(1))!).Query["X-Amz-Expires"]);
        Assert.InRange(expires, 3599, 3600);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Error:ServerException")]
    [InlineData("https://storage.cocorraapp.com/other-bucket/Uploads/Voices/a.m4a")]
    [InlineData("https://storage.cocorraapp.com/")]
    public void NothingToSign_ReturnsNull(string? stored)
    {
        Assert.Null(CreateSigner().GetReadUrl(stored));
    }

    [Fact]
    public void PercentEncodedLegacyUrl_YieldsTheDecodedKey()
    {
        Assert.Equal("Uploads/Voices/a b.m4a",
            CreateSigner().GetObjectKey($"https://storage.cocorraapp.com/{Bucket}/Uploads/Voices/a%20b.m4a"));
    }

    [Fact]
    public void PlainHttpPublicUrl_SignsAnHttpUrl()
    {
        var uri = Parse(CreateSigner("http://localhost:9000").GetReadUrl("Uploads/Voices/a.m4a")!).Uri;
        Assert.Equal("http", uri.Scheme);
        Assert.Equal("localhost", uri.Host);
        Assert.Equal(9000, uri.Port);
    }

    [Fact]
    public void MissingPublicUrl_FailsAtConstruction()
    {
        Assert.Throws<InvalidOperationException>(() => CreateSigner(""));
    }
}

public class PrivateFileUrlIssuerTests
{
    private readonly Mock<IAuditLogService> _audit = new();
    private readonly List<AuditEntry> _entries = new();
    private readonly Guid _actorId = Guid.NewGuid();

    public PrivateFileUrlIssuerTests()
    {
        _audit.Setup(a => a.RecordAsync(It.IsAny<AuditEntry>()))
            .Callback<AuditEntry>(_entries.Add)
            .Returns(Task.CompletedTask);
    }

    private PrivateFileUrlIssuer CreateIssuer()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, _actorId.ToString()), new Claim(ClaimTypes.Role, "Moderator") },
                "test"))
        };
        var signer = new StorageUrlSigner(Options.Create(new MinioSettings
        {
            AccessKey = "k",
            SecretKey = "s",
            BucketName = "cocorra-assets",
            PublicUrl = "https://storage.cocorraapp.com"
        }));
        return new PrivateFileUrlIssuer(signer, _audit.Object, new HttpContextAccessor { HttpContext = context });
    }

    private static JsonElement Details(AuditEntry entry) =>
        JsonSerializer.SerializeToElement(entry.Details, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public async Task IssuesOneUrlPerFile_InOrder_AndOneAuditEntryNamingEveryFile()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var userC = Guid.NewGuid();

        var results = await CreateIssuer().IssueAsync(PrivateFileKind.Voice, new[]
        {
            new PrivateFileRef(userA, "Uploads/Voices/a.m4a", userA),
            new PrivateFileRef(userB, null, userB),
            new PrivateFileRef(userC, "https://storage.cocorraapp.com/cocorra-assets/Uploads/Voices/c.m4a", userC)
        }, "Admin.GetUsers");

        Assert.Equal(3, results.Count);
        Assert.Contains("/Uploads/Voices/a.m4a", results[0]!.Url);
        Assert.Null(results[1]);
        Assert.Contains("/Uploads/Voices/c.m4a", results[2]!.Url);
        Assert.InRange(results[0]!.ExpiresAtUtc, DateTime.UtcNow.AddMinutes(14), DateTime.UtcNow.AddMinutes(15));

        var entry = Assert.Single(_entries);
        Assert.Equal(AuditActions.VoiceUrlIssued, entry.Action);
        Assert.Equal(_actorId, entry.ActorUserId);
        Assert.Null(entry.TargetUserId); // several users: they are listed in the details instead
        var details = Details(entry);
        Assert.Equal("Admin.GetUsers", details.GetProperty("source").GetString());
        var files = details.GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(2, files.Count);
        Assert.Equal(userA, files[0].GetProperty("userId").GetGuid());
        Assert.Equal("Uploads/Voices/a.m4a", files[0].GetProperty("objectKey").GetString());
        Assert.Equal("Uploads/Voices/c.m4a", files[1].GetProperty("objectKey").GetString());
    }

    [Fact]
    public async Task SingleFile_RecordsItsOwnerAsTheTarget()
    {
        var reportId = Guid.NewGuid();
        var reporterId = Guid.NewGuid();

        await CreateIssuer().IssueAsync(PrivateFileKind.ReportScreenshot,
            new[] { new PrivateFileRef(reportId, "Uploads/img/Support/s.png", reporterId) }, "Support.ReportScreenshotUrl");

        var entry = Assert.Single(_entries);
        Assert.Equal(AuditActions.ScreenshotUrlIssued, entry.Action);
        Assert.Equal(reporterId, entry.TargetUserId);
        Assert.Equal($"report {reportId}", entry.TargetDisplay);
        Assert.Equal("ReportScreenshot", Details(entry).GetProperty("kind").GetString());
    }

    [Fact]
    public async Task NothingIssued_WritesNoAuditEntry()
    {
        var results = await CreateIssuer().IssueAsync(PrivateFileKind.Voice,
            new[] { new PrivateFileRef(Guid.NewGuid(), null, null), new PrivateFileRef(Guid.NewGuid(), "Error:NoFile", null) },
            "Admin.GetUsers");

        Assert.All(results, Assert.Null);
        Assert.Empty(_entries);
    }

    [Theory]
    [InlineData("Uploads/Voices/a.m4a", true)]
    [InlineData("https://storage.cocorraapp.com/cocorra-assets/Uploads/Voices/a.m4a", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Error:FakeVoice", false)]
    [InlineData("https://storage.cocorraapp.com/other-bucket/Uploads/Voices/a.m4a", false)]
    public void HasFile_ReportsPresence_WithoutIssuingOrAuditing(string? stored, bool expected)
    {
        Assert.Equal(expected, CreateIssuer().HasFile(stored));
        Assert.Empty(_entries);
    }

    [Fact]
    public void NewActions_AreAcceptedByTheAuditLogFilter()
    {
        Assert.Contains(AuditActions.VoiceUrlIssued, AuditActions.All);
        Assert.Contains(AuditActions.ScreenshotUrlIssued, AuditActions.All);
    }
}
