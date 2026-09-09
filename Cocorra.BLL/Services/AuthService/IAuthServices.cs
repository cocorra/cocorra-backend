using Cocorra.BLL.DTOS.Auth;
using Cocorra.DAL.DTOS.Auth;
using Cocorra.BLL.Base;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Cocorra.BLL.Services.Auth
{
    public interface IAuthServices
    {
        /// <summary>
        /// <paramref name="device"/> comes from the X-Device-* request headers and is recorded
        /// against the user on success, so an admin can later ban every device they used.
        /// Null (older clients that don't send the headers) simply skips registration.
        /// </summary>
        Task<Response<object>> LoginAsync(LoginDto dto, DeviceInfoDto? device = null);
        Task<Response<object>> RegisterAsync(RegisterDto dto);
        Task<Response<string>> SubmitMbtiAsync(Guid userId, SubmitMbtiDto dto);
        Task<Response<string>> ForgotPasswordAsync(ForgotPasswordDto dto);
        Task<Response<string>> UpdateFcmTokenAsync(Guid userId, string fcmToken);
        Task<Response<string>> ResetPasswordAsync(ResetPasswordDto dto);
        Task<Response<string>> ReRecordVoiceAsync(string email, Microsoft.AspNetCore.Http.IFormFile voiceFile);
        Task<Response<string>> UpdatePasswordAsync(Guid userId, string currentPassword, string newPassword);
        Task<Response<string>> DeleteAccountAsync(Guid userId);
        Task<Response<AuthModel>> RefreshTokenAsync(RefreshTokenDto dto, DeviceInfoDto? device = null);
        Task<Response<string>> RevokeTokenAsync(Guid userId);
    }
}