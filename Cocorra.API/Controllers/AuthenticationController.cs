using Cocorra.BLL.DTOS.Auth;
using Cocorra.BLL.Services.Auth;
using Cocorra.BLL.Services.OTPService;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.DTOS.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Cocorra.API.Controllers
{
    [ApiController]
    public class AuthenticationController : ControllerBase
    {
        private readonly IAuthServices _authServices;
        private readonly IOTPService _otpService;

        public AuthenticationController(IAuthServices authServices, IOTPService otpService)
        {
            _otpService = otpService;
            _authServices = authServices;
        }

        [HttpPost(Router.AuthenticationRouting.Register)]
        public async Task<IActionResult> Register([FromForm] RegisterDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var result = await _authServices.RegisterAsync(dto);
            if (!result.Succeeded) return StatusCode((int)result.StatusCode, result);

            return Ok(result);
        }

        [HttpPost(Router.AuthenticationRouting.Login)]
        public async Task<IActionResult> Login([FromBody] LoginDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var result = await _authServices.LoginAsync(dto);
            if (!result.Succeeded) return StatusCode((int)result.StatusCode, result);

            return Ok(result);
        }

        [Authorize]
        [HttpPost(Router.AuthenticationRouting.SubmitMbti)]
        public async Task<IActionResult> SubmitMbti([FromBody] SubmitMbtiDto dto)
        {
            var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(userIdString, out Guid userId)) return Unauthorized();

            var result = await _authServices.SubmitMbtiAsync(userId, dto);
            if (!result.Succeeded) return StatusCode((int)result.StatusCode, result);

            return Ok(result);
        }

        [HttpPost(Router.AuthenticationRouting.ForgotPassword)]
        public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var result = await _authServices.ForgotPasswordAsync(dto);
            return Ok(result);
        }
        [Authorize(Policy = "VerificationOnly")]
        [HttpPut(Router.AuthenticationRouting.UpdateFcmToken)]
        public async Task<IActionResult> UpdateFcmToken(
            [FromQuery] string? fcmToken,
            [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] UpdateFcmTokenDto? dto)
        {
            var token = !string.IsNullOrWhiteSpace(fcmToken) ? fcmToken : dto?.FcmToken;
            if (string.IsNullOrWhiteSpace(token))
                return BadRequest("FCM token is required.");

            var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(userIdString, out Guid userId)) return Unauthorized();

            var result = await _authServices.UpdateFcmTokenAsync(userId, token);
            return StatusCode((int)result.StatusCode, result);
        }
        [HttpPost(Router.AuthenticationRouting.ResendOtp)]
        public async Task<IActionResult> ResendOtp([FromBody] string email)
        {
            var result = await _otpService.ResendOtpAsync(email);
            return StatusCode((int)result.StatusCode, result);
        }
        [HttpGet(Router.AuthenticationRouting.ConfirmEmail)]
        public async Task<IActionResult> ConfirmEmail([FromQuery] string email, [FromQuery] string otpCode)
        {
            var result = await _otpService.VerifyOtpAsync(email, otpCode);
            return StatusCode((int)result.StatusCode, result);
        }

        [HttpPost(Router.AuthenticationRouting.ResetPassword)]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var result = await _authServices.ResetPasswordAsync(dto);
            return StatusCode((int)result.StatusCode, result);
        }

        [Authorize(Policy = "VerificationOnly")]
        [HttpPost(Router.AuthenticationRouting.ReRecordVoice)]
        public async Task<IActionResult> ReRecordVoice(IFormFile voiceFile)
        {
            // SECURITY: Extract email from JWT claims, NOT from client request body.
            // This eliminates the attack vector where an anonymous user could overwrite
            // another user's voice file by passing their email.
            var email = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email)?.Value
                     ?? User.FindFirst(ClaimTypes.Email)?.Value;
            if (string.IsNullOrEmpty(email)) return Unauthorized();

            if (voiceFile == null || voiceFile.Length == 0)
                return BadRequest("No voice file uploaded.");

            var result = await _authServices.ReRecordVoiceAsync(email, voiceFile);
            return StatusCode((int)result.StatusCode, result);
        }

        [Authorize]
        [HttpPut(Router.AuthenticationRouting.UpdatePassword)]
        public async Task<IActionResult> UpdatePassword([FromBody] UpdatePasswordDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(userIdString, out Guid userId)) return Unauthorized();

            var result = await _authServices.UpdatePasswordAsync(userId, dto.CurrentPassword, dto.NewPassword);
            return StatusCode((int)result.StatusCode, result);
        }

        [Authorize]
        [HttpDelete(Router.AuthenticationRouting.DeleteAccount)]
        public async Task<IActionResult> DeleteAccount()
        {
            var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(userIdString, out Guid userId)) return Unauthorized();

            var result = await _authServices.DeleteAccountAsync(userId);
            return StatusCode((int)result.StatusCode, result);
        }
        [HttpPost(Router.AuthenticationRouting.RefreshToken)]
        public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var result = await _authServices.RefreshTokenAsync(dto);
            if (!result.Succeeded) return StatusCode((int)result.StatusCode, result);

            return Ok(result);
        }

        [Authorize]
        [HttpPost(Router.AuthenticationRouting.RevokeToken)]
        public async Task<IActionResult> RevokeToken()
        {
            var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(userIdString, out Guid userId)) return Unauthorized();

            var result = await _authServices.RevokeTokenAsync(userId);
            return StatusCode((int)result.StatusCode, result);
        }
    }
}