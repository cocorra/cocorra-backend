using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Cocorra.BLL.Services.Upload
{
    public interface IUploadVoice
    {
        /// <summary>
        /// Returns the bare object key ("Uploads/Voices/&lt;guid&gt;.ext"), never a URL: voice
        /// recordings are private and read through presigned URLs. "Error:..." on failure.
        /// </summary>
        Task<string> SaveVoice (IFormFile voiceFile);

        /// <summary>Accepts a bare object key or a legacy full URL.</summary>
        void DeleteVoice(string? voicePath);
    }
}