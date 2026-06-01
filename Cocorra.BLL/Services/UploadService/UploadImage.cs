using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Amazon.S3;
using Amazon.S3.Model;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Cocorra.BLL.Services.Upload
{
    public class UploadImage : IUploadImage
    {
        private readonly IWebHostEnvironment _env;
        private readonly IAmazonS3 _s3Client;
        private readonly Cocorra.BLL.Services.UploadService.MinioSettings _settings;
        private readonly string[] _allowedExtensions = { ".jpg", ".jpeg", ".png" };
        private const long _maxFileSize = 5 * 1024 * 1024;

        public UploadImage(IWebHostEnvironment env, IAmazonS3 s3Client, IOptions<Cocorra.BLL.Services.UploadService.MinioSettings> settings)
        {
            _env = env;
            _s3Client = s3Client;
            _settings = settings.Value;
        }

        public async Task<string> SaveImageAsync(IFormFile imageFile)
        {
            try
            {
                if (imageFile == null || imageFile.Length == 0) return "Error:NoFile";
                if (imageFile.Length > _maxFileSize) return "Error:FileTooLarge";

                string extension = Path.GetExtension(imageFile.FileName).ToLower();
                if (!_allowedExtensions.Contains(extension)) return "Error:InvalidExtension";

                var validTypes = new[] { "image/jpeg", "image/png", "image/gif", "image/jpg", "image/webp" };
                if (!validTypes.Contains(imageFile.ContentType.ToLower()) && !imageFile.ContentType.StartsWith("image/"))
                {
                    return $"Error:InvalidFileType - Received: {imageFile.ContentType}";
                }

                if (!IsValidImageSignature(imageFile)) return "Error:FakeImage";

                string fileName = Guid.NewGuid().ToString() + extension;
                var objectKey = $"Uploads/img/Profiles/{fileName}";

                using var newMemoryStream = new MemoryStream();
                await imageFile.CopyToAsync(newMemoryStream);
                newMemoryStream.Position = 0; // Reset position before upload

                var putRequest = new PutObjectRequest
                {
                    BucketName = _settings.BucketName,
                    Key = objectKey,
                    InputStream = newMemoryStream,
                    ContentType = imageFile.ContentType,
                    DisablePayloadSigning = true // Improves performance with MinIO
                };

                await _s3Client.PutObjectAsync(putRequest);

                // Return full URL
                return $"{_settings.PublicUrl}/{_settings.BucketName}/{objectKey}";
            }
            catch (Exception)
            {
                return "Error:ServerException";
            }
        }

        public void DeleteImage(string? imagePath)
        {
            if (string.IsNullOrEmpty(imagePath)) return;

            try
            {
                if (imagePath.StartsWith("http"))
                {
                    // Parse object key from full URL
                    var uri = new Uri(imagePath);
                    var objectKey = uri.AbsolutePath.Replace($"/{_settings.BucketName}/", "").TrimStart('/');
                    
                    var deleteRequest = new DeleteObjectRequest
                    {
                        BucketName = _settings.BucketName,
                        Key = objectKey
                    };

                    // Synchronously wait for it since interface returns void
                    _s3Client.DeleteObjectAsync(deleteRequest).GetAwaiter().GetResult();
                }
                else
                {
                    // Handle local fallback deletion
                    string contentPath = string.IsNullOrWhiteSpace(_env.WebRootPath)
                        ? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot")
                        : _env.WebRootPath;

                    var fullPath = Path.Combine(contentPath, imagePath.Replace("/", "\\"));
                    if (File.Exists(fullPath))
                    {
                        File.Delete(fullPath);
                    }
                }
            }
            catch
            {
            }
        }

        private bool IsValidImageSignature(IFormFile file)
        {
            try
            {
                using (var stream = file.OpenReadStream())
                {
                    var signatures = new List<byte[]>
                    {
                        new byte[] { 0xFF, 0xD8, 0xFF }, // JPEG / JPG
                        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, // PNG
                        new byte[] { 0x47, 0x49, 0x46, 0x38 }, // GIF
                        new byte[] { 0x52, 0x49, 0x46, 0x46 } // WEBP (RIFF header)
                    };

                    byte[] headerBytes = new byte[8];
                    int bytesRead = stream.Read(headerBytes, 0, headerBytes.Length);
                    if (bytesRead < 4) return false;

                    stream.Position = 0;

                    return signatures.Any(signature =>
                        headerBytes.Take(signature.Length).SequenceEqual(signature));
                }
            }
            catch
            {
                return false;
            }
        }
    }
}