using Cocorra.BLL.DTOS.Auth;
using Cocorra.BLL.Services.Auth;
using Cocorra.BLL.Services.Email;
using Cocorra.BLL.Services.Upload;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.Auth;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.RoomRepository;
using Cocorra.BLL.Base;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography;
using Cocorra.BLL.Services.EventTracking;

namespace Cocorra.BLL.Services.AuthServices
{
    public class AuthServices : ResponseHandler, IAuthServices
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<IdentityRole<Guid>> _roleManager;
        private readonly IConfiguration _configuration;
        private readonly IUploadVoice _uploadVoice;
        private readonly AppDbContext _context;
        private readonly IEmailService _emailService;
        private readonly IUploadImage _uploadImage;
        private readonly IRoomRepository _roomRepository;
        private readonly IEventTracker _eventTracker;

        public AuthServices(
            UserManager<ApplicationUser> userManager,
            RoleManager<IdentityRole<Guid>> roleManager,
            IConfiguration configuration,
            IUploadVoice uploadVoice,
            IEmailService emailService,
            IUploadImage uploadImage,
            AppDbContext context,
            IRoomRepository roomRepository,
            IEventTracker eventTracker)
        {
            _uploadImage = uploadImage;
            _context = context;
            _uploadVoice = uploadVoice;
            _userManager = userManager;
            _roleManager = roleManager;
            _emailService = emailService;
            _configuration = configuration;
            _roomRepository = roomRepository;
            _eventTracker = eventTracker;
        }

        public async Task<Response<object>> RegisterAsync(RegisterDto dto)
        {
            var strategy = _context.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                using var transaction = await _context.Database.BeginTransactionAsync();
                string? voicePathToDelete = null;
                string? profilePicturePathToDelete = null;

                try
                {
                    var existingUser = await _userManager.FindByEmailAsync(dto.Email!);
                    if (existingUser is not null)
                        return BadRequest<object>("Email is already registered");

                    var voicePath = await _uploadVoice.SaveVoice(dto.VoiceVerification!);
                    if (voicePath.StartsWith("Error"))
                        return BadRequest<object>(voicePath);
                    voicePathToDelete = voicePath;
                    var profilePicturePath = await _uploadImage.SaveImageAsync(dto.ProfilePicture!);
                    if (profilePicturePath.StartsWith("Error"))
                    {
                        _uploadVoice.DeleteVoice(voicePathToDelete);
                        return BadRequest<object>(profilePicturePath);
                    }
                    profilePicturePathToDelete = profilePicturePath;
                    var user = new ApplicationUser
                    {
                        UserName = dto.Email,
                        Email = dto.Email,
                        FirstName = dto.FirstName!,
                        LastName = dto.LastName!,
                        Age = dto.Age,
                        Status = UserStatus.Pending,
                        VoiceVerificationPath = voicePath,
                        EmailConfirmed = false,
                        CreatedAt = DateTime.UtcNow,
                        ProfilePicturePath = profilePicturePath
                    };

                    var result = await _userManager.CreateAsync(user, dto.Password!);
                    if (!result.Succeeded)
                    {
                        _uploadVoice.DeleteVoice(voicePathToDelete);
                        var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                        return BadRequest<object>($"Registration failed: {errors}");
                    }

                    if (!await _roleManager.RoleExistsAsync("User"))
                        await _roleManager.CreateAsync(new IdentityRole<Guid>("User"));

                    var roleResult = await _userManager.AddToRoleAsync(user, "User");
                    if (!roleResult.Succeeded)
                        throw new Exception("Failed to assign role");
                        var otpCode = await _userManager.GenerateTwoFactorTokenAsync(
                            user,
                            TokenOptions.DefaultEmailProvider
                        );
                    var baseUrl = _configuration["AppSettings:BaseUrl"];
                    var fullImagePath = $"{baseUrl}/System/388f7e03b835e6ca1f7c156816047a360bf18efe.png"; 
                    var emailBody = GetOtpHtmlTemplate(user.FirstName!, email: user.Email!, otpCode, fullImagePath);
                    await _emailService.SendEmailAsync(user.Email!, "Registration Received", emailBody);
                    var (restrictedToken, restrictedRoles) = await GenerateJwtToken(user);
                    var restrictedRefreshToken = GenerateRefreshToken();
                    user.RefreshToken = restrictedRefreshToken;
                    user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(7);
                    await _userManager.UpdateAsync(user);

                    _eventTracker.Track(EventTypes.UserRegistered, user.Id);
                    _eventTracker.Track(EventTypes.VoiceVerificationSubmitted, user.Id);

                    var restrictedAuth = new AuthModel
                    {
                        Email = user.Email,
                        Username = user.UserName,
                        Token = new JwtSecurityTokenHandler().WriteToken(restrictedToken),
                        ExpiresOn = restrictedToken.ValidTo,
                        Roles = restrictedRoles.ToList(),
                        RefreshToken = restrictedRefreshToken,
                        RefreshTokenExpiration = user.RefreshTokenExpiryTime,
                        UserStatus = user.Status.ToString()
                    };

                    await transaction.CommitAsync();

                    return Created<object>(restrictedAuth, meta: new { message = "Registration successful! Check Your Email ." });
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    _uploadVoice.DeleteVoice(voicePathToDelete);
                    _uploadImage.DeleteImage(profilePicturePathToDelete);
                    return BadRequest<object>($"Error: {ex.Message} -- Internal: {ex.InnerException?.Message}");
                }
            });
        }

        public async Task<Response<object>> LoginAsync(LoginDto dto)
        {
            var user = await _userManager.FindByEmailAsync(dto.Email!);
            if (user == null)
            {
                return BadRequest<object>("Invalid Email or Password");
            }

            // SECURITY: Check lockout BEFORE password verification.
            // Prevents locked-out users from exercising the password hash path,
            // and stops AccessFailedCount from incrementing during an active lockout.
            if (await _userManager.IsLockedOutAsync(user))
            {
                return Forbidden<object>("Account is locked or banned.", new { lockoutEnd = user.LockoutEnd });
            }

            if (!await _userManager.CheckPasswordAsync(user, dto.Password!))
            {
                return BadRequest<object>("Invalid Email or Password");
            }

            // Clear any accumulated failed access attempts on successful login.
            await _userManager.ResetAccessFailedCountAsync(user);

            if (!user.EmailConfirmed)
            {
                return BadRequest<object>("Please confirm your email before logging in.");
            }
            switch (user.Status)
            {
                case UserStatus.Pending:
                case UserStatus.ReRecord:
                    // SECURITY: Issue a RESTRICTED JWT for verification-stage users.
                    // The JWT contains VerificationStatus=Pending/ReRecord, which is enforced
                    // by the "VerificationOnly" policy. All other endpoints use the default
                    // "FullAccess" policy (requires VerificationStatus=Active) and will reject this token.
                    var (restrictedToken, restrictedRoles) = await GenerateJwtToken(user);
                    var restrictedRefreshToken = GenerateRefreshToken();
                    user.RefreshToken = restrictedRefreshToken;
                    user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(7);
                    await _userManager.UpdateAsync(user);

                    var restrictedAuth = new AuthModel
                    {
                        Email = user.Email,
                        Username = user.UserName,
                        Token = new JwtSecurityTokenHandler().WriteToken(restrictedToken),
                        ExpiresOn = restrictedToken.ValidTo,
                        Roles = restrictedRoles.ToList(),
                        RefreshToken = restrictedRefreshToken,
                        RefreshTokenExpiration = user.RefreshTokenExpiryTime,
                        UserStatus = user.Status.ToString()
                    };
                    return Success<object>(restrictedAuth, meta: new { userStatus = user.Status.ToString() });
                case UserStatus.Rejected:
                    return BadRequest<object>(new { userStatus = user.Status.ToString() }, "Your account has been rejected.");
                case UserStatus.Banned:
                    return BadRequest<object>(new { userStatus = user.Status.ToString() }, "Your account has been banned.");
                case UserStatus.Active:
                    break;
                default:
                    return BadRequest<object>("Invalid user status.");
            }

            var (jwtToken, roles) = await GenerateJwtToken(user);
            var refreshToken = GenerateRefreshToken();
            user.RefreshToken = refreshToken;
            user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(7);
            await _userManager.UpdateAsync(user);

            var authModel = new AuthModel
            {
                Email = user.Email,
                Username = user.UserName,
                Token = new JwtSecurityTokenHandler().WriteToken(jwtToken),
                ExpiresOn = jwtToken.ValidTo,
                Roles = roles.ToList(),
                RefreshToken = refreshToken,
                RefreshTokenExpiration = user.RefreshTokenExpiryTime,
                UserStatus = user.Status.ToString()
            };
            return Success<object>(authModel);
        }

        public async Task<Response<string>> SubmitMbtiAsync(Guid userId, SubmitMbtiDto dto)
        {
            var user = await _userManager.FindByIdAsync(userId.ToString());
            if (user == null) return BadRequest<string>("User not found.");

            user.MBTI = dto.MBTI;
            var result = await _userManager.UpdateAsync(user);

            if (result.Succeeded)
            {
                _eventTracker.Track(EventTypes.MbtiSubmitted, userId, new { mbti = dto.MBTI });
                return Success("MBTI updated successfully.");
            }

            return BadRequest<string>("Failed to update MBTI.");
        }

        public async Task<Response<string>> ForgotPasswordAsync(ForgotPasswordDto dto)
        {
            var user = await _userManager.FindByEmailAsync(dto.Email!);
            if (user == null)
                return Success("If your email is registered, you will receive a reset link.");

            var otpCode = await _userManager.GenerateTwoFactorTokenAsync(
                user,
                TokenOptions.DefaultEmailProvider
            );

            var baseUrl = _configuration["AppSettings:BaseUrl"];
            var fullImagePath = $"{baseUrl}/System/388f7e03b835e6ca1f7c156816047a360bf18efe.png";
            var emailBody = GetPasswordResetHtmlTemplate(user.FirstName!, user.Email!, otpCode, fullImagePath);
            await _emailService.SendEmailAsync(user.Email!, "Password Reset Code", emailBody);

            return Success("If your email is registered, you will receive a password reset code shortly.");
        }


        private async Task<(JwtSecurityToken Token, IList<string> Roles)> GenerateJwtToken(ApplicationUser user)
        {
            var userRoles = await _userManager.GetRolesAsync(user);
            var claims = new List<Claim>
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email!),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.UserName!),
                // profile Picture URL claim
                new Claim("profilePicture", string.IsNullOrEmpty(user.ProfilePicturePath) ? "" : $"{_configuration["AppSettings:BaseUrl"]}/{user.ProfilePicturePath.Replace("\\", "/")}"),
                // SECURITY: Embed verification status for policy-based authorization.
                // This claim determines whether the user can access full app features (Active)
                // or only verification endpoints (Pending/ReRecord).
                new Claim("VerificationStatus", user.Status.ToString())
            };

            foreach (var role in userRoles) claims.Add(new Claim(ClaimTypes.Role, role));

            var key = Encoding.ASCII.GetBytes(_configuration["JWTSetting:securityKey"]!);
            var authKey = new SymmetricSecurityKey(key);

            var token = new JwtSecurityToken(
                audience: _configuration["JWTSetting:ValidAudience"],
                issuer: _configuration["JWTSetting:ValidIssuer"],
                expires: DateTime.UtcNow.AddDays(1),
                claims: claims,
                signingCredentials: new SigningCredentials(authKey, SecurityAlgorithms.HmacSha256)
            );

            return (token, userRoles);
        }
        public async Task<Response<string>> UpdateFcmTokenAsync(Guid userId, string fcmToken)
        {
            var user = await _userManager.FindByIdAsync(userId.ToString());
            if (user == null) return BadRequest<string>("User not found.");

            user.FcmToken = fcmToken;
            await _userManager.UpdateAsync(user);

            return Success("FCM Token updated successfully.");
        }
        public async Task<Response<string>> ResetPasswordAsync(ResetPasswordDto dto)
        {
            var user = await _userManager.FindByEmailAsync(dto.Email);
            if (user == null)
                return BadRequest<string>("Invalid request.");

            var isValidOtp = await _userManager.VerifyTwoFactorTokenAsync(
                user,
                TokenOptions.DefaultEmailProvider,
                dto.OtpCode
            );

            if (!isValidOtp)
                return BadRequest<string>("Invalid or expired OTP code.");

            var resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);
            var result = await _userManager.ResetPasswordAsync(user, resetToken, dto.NewPassword);

            if (!result.Succeeded)
            {
                var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                return BadRequest<string>($"Password reset failed: {errors}");
            }

            return Success("Password has been reset successfully.");
        }
        private string GetOtpHtmlTemplate(string userName, string email, string otpCode, string baseUrl)
        {
            // استخدمنا $$""" لكي نتجاهل أقواس الـ CSS العادية { }
            // ونستخدم المتغيرات بين قوسين مزدوجين {{ }}
            return $$"""
    <!DOCTYPE html>
    <html lang="en">

    <head>
        <meta charset="UTF-8">
        <meta name="viewport" content="width=device-width, initial-scale=1.0">
        <title>Email Verification</title>
        <style>
            body { margin: 0; padding: 0; background-color: #e0e0e0; display: flex; justify-content: center; align-items: center; min-height: 100vh; font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; }
            .container { width: 100%; max-width: 400px; text-align: center; box-shadow: 0 4px 10px rgba(0, 0, 0, 0.2); }
            .header { background-color: #4f5b49; padding: 30px 0; }
            .logo-container { width: 100px; height: 100px; margin: 0 auto; border: 2px solid white; border-radius: 8px; overflow: hidden; background-color: #c5d1ba; }
            .logo-container img { width: 100%; height: 100%; object-fit: cover; }
            .content { background-color: #a0b19d; padding: 25px 20px 40px; color: white; }
            .greeting { margin: 0 0 15px 0; font-size: 26px; font-weight: bold; }
            .email-box { background-color: white; color: #333; padding: 12px 20px; border-radius: 30px; display: inline-block; font-weight: bold; font-size: 18px; margin-bottom: 25px; width: 85%; box-sizing: border-box; }
            .instruction { margin: 0 0 25px 0; font-size: 16px; line-height: 1.4; font-weight: 600; }
            .code-box { background-color: white; color: black; font-size: 32px; font-weight: 900; letter-spacing: 8px; padding: 20px; border-radius: 15px; margin-bottom: 25px; display: inline-block; width: 85%; box-sizing: border-box; }
            .footer { margin: 0; font-size: 14px; font-weight: bold; }
        </style>
    </head>

    <body>
        <div class="container">
            <div class="header">
                <div class="logo-container">
                    <img src="{{baseUrl}}" alt="Cocorra">
                </div>
            </div>

            <div class="content">
                <h1 class="greeting">Hello {{userName}}</h1>

                <div class="email-box">
                    {{email}}
                </div>

                <p class="instruction">
                    The current code is for<br>
                    the verification process to complete<br>
                    your account registration.
                </p>

                <div class="code-box">
                    {{otpCode}}
                </div>

                <p class="footer">
                    This code is valid for 10 minutes.
                </p>
            </div>
        </div>
    </body>

    </html>
    """;
        }
        private string GetPasswordResetHtmlTemplate(string userName, string email, string otpCode, string baseUrl)
        {
            return $$"""
    <!DOCTYPE html>
    <html lang="en">

    <head>
        <meta charset="UTF-8">
        <meta name="viewport" content="width=device-width, initial-scale=1.0">
        <title>Password Reset</title>
        <style>
            body { margin: 0; padding: 0; background-color: #e0e0e0; display: flex; justify-content: center; align-items: center; min-height: 100vh; font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; }
            .container { width: 100%; max-width: 400px; text-align: center; box-shadow: 0 4px 10px rgba(0, 0, 0, 0.2); }
            .header { background-color: #4f5b49; padding: 30px 0; }
            .logo-container { width: 100px; height: 100px; margin: 0 auto; border: 2px solid white; border-radius: 8px; overflow: hidden; background-color: #c5d1ba; }
            .logo-container img { width: 100%; height: 100%; object-fit: cover; }
            .content { background-color: #a0b19d; padding: 25px 20px 40px; color: white; }
            .greeting { margin: 0 0 15px 0; font-size: 26px; font-weight: bold; }
            .email-box { background-color: white; color: #333; padding: 12px 20px; border-radius: 30px; display: inline-block; font-weight: bold; font-size: 18px; margin-bottom: 25px; width: 85%; box-sizing: border-box; }
            .instruction { margin: 0 0 25px 0; font-size: 16px; line-height: 1.4; font-weight: 600; }
            .code-box { background-color: white; color: black; font-size: 32px; font-weight: 900; letter-spacing: 8px; padding: 20px; border-radius: 15px; margin-bottom: 25px; display: inline-block; width: 85%; box-sizing: border-box; }
            .footer { margin: 0; font-size: 14px; font-weight: bold; }
        </style>
    </head>

    <body>
        <div class="container">
            <div class="header">
                <div class="logo-container">
                    <img src="{{baseUrl}}" alt="Cocorra">
                </div>
            </div>

            <div class="content">
                <h1 class="greeting">Hello {{userName}}</h1>

                <div class="email-box">
                    {{email}}
                </div>

                <p class="instruction">
                    Use the code below to<br>
                    reset your password.
                </p>

                <div class="code-box">
                    {{otpCode}}
                </div>

                <p class="footer">
                    This code is valid for 10 minutes.
                </p>
            </div>
        </div>
    </body>

    </html>
    """;
        }

        public async Task<Response<string>> ReRecordVoiceAsync(string email, Microsoft.AspNetCore.Http.IFormFile voiceFile)
        {
            var user = await _userManager.FindByEmailAsync(email);
            if (user == null) return BadRequest<string>("User not found.");

            if (user.Status != UserStatus.ReRecord)
                return BadRequest<string>("You can only re-record your voice when your status is 'ReRecord'.");

            _uploadVoice.DeleteVoice(user.VoiceVerificationPath);

            var newVoicePath = await _uploadVoice.SaveVoice(voiceFile);
            if (newVoicePath.StartsWith("Error"))
                return BadRequest<string>(newVoicePath);

            user.VoiceVerificationPath = newVoicePath;
            user.Status = UserStatus.Pending;

            var result = await _userManager.UpdateAsync(user);
            if (!result.Succeeded)
                return BadRequest<string>("Failed to update voice verification.");

            _eventTracker.Track(EventTypes.VoiceVerificationSubmitted, user.Id);

            return Success("Voice re-recorded successfully. Your account is now pending review.");
        }

        public async Task<Response<string>> UpdatePasswordAsync(Guid userId, string currentPassword, string newPassword)
        {
            var user = await _userManager.FindByIdAsync(userId.ToString());
            if (user == null) return BadRequest<string>("User not found.");

            var result = await _userManager.ChangePasswordAsync(user, currentPassword, newPassword);
            if (!result.Succeeded)
            {
                var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                return BadRequest<string>($"Password update failed: {errors}");
            }

            return Success("Password updated successfully.");
        }

        public async Task<Response<string>> DeleteAccountAsync(Guid userId)
        {
            var user = await _userManager.FindByIdAsync(userId.ToString());
            if (user == null) return BadRequest<string>("User not found.");

            // End any Active/Live rooms hosted by this user to prevent ghost rooms
            var activeRooms = await _context.Rooms
                .Where(r => r.HostId == userId &&
                       (r.Status == RoomStatus.Live || r.Status == RoomStatus.Scheduled))
                .ToListAsync();

            foreach (var room in activeRooms)
            {
                room.Status = RoomStatus.Ended;
                room.UpdatedAt = DateTime.UtcNow;
            }

            if (activeRooms.Any())
            {
                await _context.SaveChangesAsync();
            }

            // Clean up all Restrict-FK rows to allow user deletion
            await _context.FriendRequests
                .Where(fr => fr.SenderId == userId || fr.ReceiverId == userId)
                .ExecuteDeleteAsync();

            await _context.Messages
                .Where(m => m.SenderId == userId || m.ReceiverId == userId)
                .ExecuteDeleteAsync();

            await _context.UserBlocks
                .Where(ub => ub.BlockerId == userId || ub.BlockedId == userId)
                .ExecuteDeleteAsync();

            await _context.Notifications
                .Where(n => n.UserId == userId)
                .ExecuteDeleteAsync();

            try
            {
                _eventTracker.Track(EventTypes.AccountDeleted, userId, new { reason = "User requested deletion" });
                var result = await _userManager.DeleteAsync(user);

                if (!result.Succeeded)
                {
                    var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                    return BadRequest<string>($"Account deletion failed: {errors}");
                }

                return Success("Account deleted successfully.");
            }
            catch (DbUpdateException)
            {
                return BadRequest<string>("Cannot delete account due to remaining database references. Please contact support.");
            }
        }

        private string GenerateRefreshToken()
        {
            var randomNumber = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(randomNumber);
            return Convert.ToBase64String(randomNumber);
        }

        public async Task<Response<AuthModel>> RefreshTokenAsync(RefreshTokenDto dto)
        {
            var user = await _userManager.Users
                .SingleOrDefaultAsync(u => u.RefreshToken == dto.RefreshToken);

            if (user == null || user.RefreshTokenExpiryTime <= DateTime.UtcNow)
            {
                return BadRequest<AuthModel>("Invalid or expired refresh token.");
            }

            // SECURITY: Prevent banned/locked-out users from silently refreshing tokens.
            if (await _userManager.IsLockedOutAsync(user))
            {
                // Invalidate the compromised refresh token.
                user.RefreshToken = null;
                await _userManager.UpdateAsync(user);
                return Forbidden<AuthModel>("Account is locked or banned.", new { lockoutEnd = user.LockoutEnd });
            }

            var (newAccessToken, roles) = await GenerateJwtToken(user);
            var newRefreshToken = GenerateRefreshToken();

            user.RefreshToken = newRefreshToken;
            user.RefreshTokenExpiryTime = DateTime.UtcNow.AddDays(7);
            await _userManager.UpdateAsync(user);

            return Success(new AuthModel
            {
                Token = new JwtSecurityTokenHandler().WriteToken(newAccessToken),
                RefreshToken = newRefreshToken,
                ExpiresOn = newAccessToken.ValidTo,
                RefreshTokenExpiration = user.RefreshTokenExpiryTime,
                Email = user.Email,
                Username = user.UserName,
                Roles = roles.ToList(),
                UserStatus = user.Status.ToString()
            });
        }

        public async Task<Response<string>> RevokeTokenAsync(Guid userId)
        {
            var user = await _userManager.FindByIdAsync(userId.ToString());
            if (user == null) return BadRequest<string>("Invalid user");

            if (string.IsNullOrEmpty(user.RefreshToken))
                return Success<string>("Token already revoked.");

            user.RefreshToken = null;
            await _userManager.UpdateAsync(user);

            return Success<string>("Token revoked successfully.");
        }
    }
}