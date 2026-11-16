using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;

namespace Cocorra.BLL.Services.Upload
{
    public interface IUploadImage
    {
        /// <summary>Public images (profiles, rooms). Returns the full public URL, or "Error:...".</summary>
        Task<string> SaveImageAsync(IFormFile imageFile, string subFolder = "Profiles");

        /// <summary>
        /// Private images (support/report screenshots). Returns the bare object key, never a URL:
        /// readers get a presigned URL through IStorageUrlSigner. "Error:..." on failure.
        /// </summary>
        Task<string> SavePrivateImageAsync(IFormFile imageFile, string subFolder);

        /// <summary>Accepts a full URL or a bare object key.</summary>
        void DeleteImage(string? imagePath);
    }
}