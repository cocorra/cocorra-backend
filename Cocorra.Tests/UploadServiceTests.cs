using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using Cocorra.BLL.Services.Upload;
using Cocorra.BLL.Services.UploadService;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class UploadServiceTests
{
    private readonly Mock<IWebHostEnvironment> _envMock = new();
    private readonly Mock<IAmazonS3> _s3Mock = new();
    private readonly MinioSettings _settings = new()
    {
        BucketName = "cocorra-bucket",
        PublicUrl = "https://s3.cocorra.com"
    };

    private UploadImage CreateImageService()
    {
        return new UploadImage(_envMock.Object, _s3Mock.Object, Options.Create(_settings));
    }

    private UploadVoice CreateVoiceService()
    {
        return new UploadVoice(_envMock.Object, _s3Mock.Object, Options.Create(_settings));
    }

    private static IFormFile CreateMockFormFile(string fileName, string contentType, byte[] content)
    {
        var stream = new MemoryStream(content);
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.FileName).Returns(fileName);
        fileMock.Setup(f => f.ContentType).Returns(contentType);
        fileMock.Setup(f => f.Length).Returns(content.Length);
        fileMock.Setup(f => f.OpenReadStream()).Returns(() => new MemoryStream(content));
        fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns((Stream target, CancellationToken ct) => stream.CopyToAsync(target, ct));
        return fileMock.Object;
    }

    // =========================================================================
    // UploadImage Tests
    // =========================================================================

    [Fact]
    public async Task UploadImage_NullOrEmptyFile_ReturnsErrorNoFile()
    {
        var service = CreateImageService();
        var result = await service.SaveImageAsync(null!);
        Assert.Equal("Error:NoFile", result);

        var emptyMock = new Mock<IFormFile>();
        emptyMock.Setup(f => f.Length).Returns(0);
        var result2 = await service.SaveImageAsync(emptyMock.Object);
        Assert.Equal("Error:NoFile", result2);
    }

    [Fact]
    public async Task UploadImage_FileTooLarge_ReturnsErrorFileTooLarge()
    {
        var service = CreateImageService();
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(6 * 1024 * 1024); // 6MB > 5MB

        var result = await service.SaveImageAsync(fileMock.Object);
        Assert.Equal("Error:FileTooLarge", result);
    }

    [Fact]
    public async Task UploadImage_InvalidExtension_ReturnsErrorInvalidExtension()
    {
        var service = CreateImageService();
        var file = CreateMockFormFile("test.exe", "image/png", new byte[] { 1, 2, 3 });

        var result = await service.SaveImageAsync(file);
        Assert.Equal("Error:InvalidExtension", result);
    }

    [Fact]
    public async Task UploadImage_FakeImageSignature_ReturnsErrorFakeImage()
    {
        var service = CreateImageService();
        var fakeBytes = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        var file = CreateMockFormFile("avatar.png", "image/png", fakeBytes);

        var result = await service.SaveImageAsync(file);
        Assert.Equal("Error:FakeImage", result);
    }

    [Fact]
    public async Task UploadImage_ValidJpeg_UploadsToS3AndReturnsUrl()
    {
        var service = CreateImageService();
        // Valid JPEG header: FF D8 FF E0
        var jpegBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 };
        var file = CreateMockFormFile("avatar.jpg", "image/jpeg", jpegBytes);

        _s3Mock.Setup(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), default))
            .ReturnsAsync(new PutObjectResponse());

        var result = await service.SaveImageAsync(file, "Avatars");

        Assert.StartsWith("https://s3.cocorra.com/cocorra-bucket/Uploads/img/Avatars/", result);
        _s3Mock.Verify(s => s.PutObjectAsync(It.Is<PutObjectRequest>(r =>
            r.BucketName == "cocorra-bucket" &&
            r.ContentType == "image/jpeg" &&
            r.Key.StartsWith("Uploads/img/Avatars/")), default), Times.Once);
    }

    [Fact]
    public void DeleteImage_RemoteHttpUrl_DeletesFromS3()
    {
        var service = CreateImageService();
        var url = "https://s3.cocorra.com/cocorra-bucket/Uploads/img/Profiles/sample.jpg";

        _s3Mock.Setup(s => s.DeleteObjectAsync(It.IsAny<DeleteObjectRequest>(), default))
            .ReturnsAsync(new DeleteObjectResponse());

        service.DeleteImage(url);

        _s3Mock.Verify(s => s.DeleteObjectAsync(It.Is<DeleteObjectRequest>(r =>
            r.BucketName == "cocorra-bucket" &&
            r.Key == "Uploads/img/Profiles/sample.jpg"), default), Times.Once);
    }

    // =========================================================================
    // UploadVoice Tests
    // =========================================================================

    [Fact]
    public async Task UploadVoice_NullOrEmptyFile_ReturnsErrorNoFile()
    {
        var service = CreateVoiceService();
        var result = await service.SaveVoice(null!);
        Assert.Equal("Error:NoFile", result);

        var emptyMock = new Mock<IFormFile>();
        emptyMock.Setup(f => f.Length).Returns(0);
        var result2 = await service.SaveVoice(emptyMock.Object);
        Assert.Equal("Error:NoFile", result2);
    }

    [Fact]
    public async Task UploadVoice_FileTooLarge_ReturnsErrorFileTooLarge()
    {
        var service = CreateVoiceService();
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.Length).Returns(4 * 1024 * 1024); // 4MB > 3MB

        var result = await service.SaveVoice(fileMock.Object);
        Assert.Equal("Error:FileTooLarge", result);
    }

    [Fact]
    public async Task UploadVoice_InvalidExtension_ReturnsErrorInvalidExtension()
    {
        var service = CreateVoiceService();
        var file = CreateMockFormFile("voice.txt", "audio/mp3", new byte[] { 1, 2, 3 });

        var result = await service.SaveVoice(file);
        Assert.Equal("Error:InvalidExtension", result);
    }

    [Fact]
    public async Task UploadVoice_NonAudioContentType_ReturnsErrorInvalidFileType()
    {
        var service = CreateVoiceService();
        var file = CreateMockFormFile("voice.mp3", "application/octet-stream", new byte[] { 1, 2, 3 });

        var result = await service.SaveVoice(file);
        Assert.Equal("Error:InvalidFileType", result);
    }

    [Fact]
    public async Task UploadVoice_FakeSignature_ReturnsErrorFakeVoice()
    {
        var service = CreateVoiceService();
        var fakeBytes = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00 };
        var file = CreateMockFormFile("voice.mp3", "audio/mpeg", fakeBytes);

        var result = await service.SaveVoice(file);
        Assert.Equal("Error:FakeVoice", result);
    }

    [Fact]
    public async Task UploadVoice_ValidMp3_UploadsToS3AndReturnsUrl()
    {
        var service = CreateVoiceService();
        // ID3 header for MP3: 0x49, 0x44, 0x33
        var mp3Bytes = new byte[] { 0x49, 0x44, 0x33, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        var file = CreateMockFormFile("voice.mp3", "audio/mpeg", mp3Bytes);

        _s3Mock.Setup(s => s.PutObjectAsync(It.IsAny<PutObjectRequest>(), default))
            .ReturnsAsync(new PutObjectResponse());

        var result = await service.SaveVoice(file);

        Assert.StartsWith("https://s3.cocorra.com/cocorra-bucket/Uploads/Voices/", result);
        _s3Mock.Verify(s => s.PutObjectAsync(It.Is<PutObjectRequest>(r =>
            r.BucketName == "cocorra-bucket" &&
            r.ContentType == "audio/mpeg" &&
            r.Key.StartsWith("Uploads/Voices/")), default), Times.Once);
    }

    [Fact]
    public void DeleteVoice_RemoteHttpUrl_DeletesFromS3()
    {
        var service = CreateVoiceService();
        var url = "https://s3.cocorra.com/cocorra-bucket/Uploads/Voices/sample.mp3";

        _s3Mock.Setup(s => s.DeleteObjectAsync(It.IsAny<DeleteObjectRequest>(), default))
            .ReturnsAsync(new DeleteObjectResponse());

        service.DeleteVoice(url);

        _s3Mock.Verify(s => s.DeleteObjectAsync(It.Is<DeleteObjectRequest>(r =>
            r.BucketName == "cocorra-bucket" &&
            r.Key == "Uploads/Voices/sample.mp3"), default), Times.Once);
    }
}
