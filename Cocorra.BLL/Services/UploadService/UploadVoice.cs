using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
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
    public class UploadVoice : IUploadVoice
    {
        private readonly IWebHostEnvironment _env;
        private readonly IAmazonS3 _s3Client;
        private readonly Cocorra.BLL.Services.UploadService.MinioSettings _settings;
        private readonly string[] _allowedExtensions = { ".mp3", ".wav", ".m4a", ".ogg", ".aac" };
        private const long _maxFileSize = 3 * 1024 * 1024;

        public UploadVoice(IWebHostEnvironment env, IAmazonS3 s3Client, IOptions<Cocorra.BLL.Services.UploadService.MinioSettings> settings)
        {
            _env = env;
            _s3Client = s3Client;
            _settings = settings.Value;
        }

        public async Task<string> SaveVoice(IFormFile voiceFile)
        {
            try
            {
                if (voiceFile == null || voiceFile.Length == 0) return "Error:NoFile";
                if (voiceFile.Length > _maxFileSize) return "Error:FileTooLarge"; 

                string extension = Path.GetExtension(voiceFile.FileName).ToLower();
                if (!_allowedExtensions.Contains(extension)) return "Error:InvalidExtension"; 

                if (!voiceFile.ContentType.StartsWith("audio/")) return "Error:InvalidFileType";
                if (!IsValidVoiceSignature(voiceFile)) return "Error:FakeVoice";

                string fileName = Guid.NewGuid().ToString() + extension;
                var objectKey = $"Uploads/Voices/{fileName}";

                using var newMemoryStream = new MemoryStream();
                await voiceFile.CopyToAsync(newMemoryStream);
                newMemoryStream.Position = 0; // Reset position before upload

                var putRequest = new PutObjectRequest
                {
                    BucketName = _settings.BucketName,
                    Key = objectKey,
                    InputStream = newMemoryStream,
                    ContentType = voiceFile.ContentType,
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

        public void DeleteVoice(string? voicePath)
        {
            if (string.IsNullOrEmpty(voicePath)) return;

            try
            {
                if (voicePath.StartsWith("http"))
                {
                    // Parse object key from full URL
                    var uri = new Uri(voicePath);
                    var objectKey = uri.AbsolutePath.Replace($"/{_settings.BucketName}/", "").TrimStart('/');
                    
                    var deleteRequest = new DeleteObjectRequest
                    {
                        BucketName = _settings.BucketName,
                        Key = objectKey
                    };

                    _s3Client.DeleteObjectAsync(deleteRequest).GetAwaiter().GetResult();
                }
                else
                {
                    string contentPath = string.IsNullOrWhiteSpace(_env.WebRootPath)
                        ? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot")
                        : _env.WebRootPath;

                    var fullPath = Path.Combine(contentPath, voicePath.Replace("/", "\\"));
                    if (File.Exists(fullPath))
                    {
                        File.Delete(fullPath);
                    }
                }
            }
            catch { }
        }

        private bool IsValidVoiceSignature(IFormFile file)
        {
            try
            {
                using (var stream = file.OpenReadStream())
                {
                    byte[] headerBytes = new byte[12];
                    int bytesRead = stream.Read(headerBytes, 0, headerBytes.Length);
                    stream.Position = 0;
                    
                    if (bytesRead < 2) return false;

                    var signatures = new List<byte[]>
                    {
                        new byte[] { 0x49, 0x44, 0x33 }, // MP3 (ID3)
                        new byte[] { 0xFF, 0xFB }, // MP3 (MPEG audio frame)
                        new byte[] { 0xFF, 0xF3 }, // MP3 (MPEG audio frame)
                        new byte[] { 0xFF, 0xF2 }, // MP3 (MPEG audio frame)
                        new byte[] { 0x52, 0x49, 0x46, 0x46 }, // WAV (RIFF)
                        new byte[] { 0x4F, 0x67, 0x67, 0x53 }, // OGG
                        new byte[] { 0xFF, 0xF1 }, // AAC (ADTS)
                        new byte[] { 0xFF, 0xF9 }  // AAC (ADTS alt)
                    };

                    if (signatures.Any(sig => headerBytes.Take(sig.Length).SequenceEqual(sig)))
                        return true;

                    if (bytesRead >= 8)
                    {
                        byte[] ftypMarker = { 0x66, 0x74, 0x79, 0x70 }; // "ftyp"
                        if (headerBytes.Skip(4).Take(4).SequenceEqual(ftypMarker))
                            return true;
                    }

                    return false;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}