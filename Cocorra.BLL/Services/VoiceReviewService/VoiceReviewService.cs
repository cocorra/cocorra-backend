using Cocorra.BLL.Base;
using Cocorra.BLL.Services.AdminService;
using Cocorra.BLL.Services.AuditLogs;
using Cocorra.BLL.Services.Storage;
using Cocorra.DAL.DTOS.StorageDto;
using Cocorra.DAL.DTOS.VoiceReviewDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.UserRepository;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Cocorra.BLL.Services.VoiceReviewService
{
    public class VoiceReviewService : ResponseHandler, IVoiceReviewService
    {
        private const string ProtectorPurpose = "Cocorra.VoiceReview.Ref.v1";
        private static readonly TimeSpan ReviewRefLifetime = TimeSpan.FromHours(12);
        private const int MaxPageSize = 50;

        private readonly IUserRepository _userRepository;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IAdminService _adminService;
        private readonly IPrivateFileUrlIssuer _fileUrls;
        private readonly IAuditLogService _auditLog;
        private readonly ITimeLimitedDataProtector _protector;
        private readonly byte[] _shortRefKey;

        public VoiceReviewService(
            IUserRepository userRepository,
            UserManager<ApplicationUser> userManager,
            IAdminService adminService,
            IPrivateFileUrlIssuer fileUrls,
            IAuditLogService auditLog,
            IDataProtectionProvider dataProtectionProvider,
            IConfiguration configuration)
        {
            _userRepository = userRepository;
            _userManager = userManager;
            _adminService = adminService;
            _fileUrls = fileUrls;
            _auditLog = auditLog;
            _protector = dataProtectionProvider
                .CreateProtector(ProtectorPurpose)
                .ToTimeLimitedDataProtector();

            var jwtKey = configuration["JWTSetting:securityKey"];
            if (string.IsNullOrWhiteSpace(jwtKey))
                throw new InvalidOperationException("JWTSetting:securityKey is required to derive voice review references.");

            _shortRefKey = Encoding.UTF8.GetBytes(jwtKey);
        }

        public async Task<PagedResponse<VoiceReviewItemDto>> GetPendingAsync(int pageNumber = 1, int pageSize = 20)
        {
            pageNumber = Math.Max(1, pageNumber);
            pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

            var (totalCount, records) = await _userRepository.GetPendingVoiceReviewsAsync(pageNumber, pageSize);

            var items = records.Select(r => new VoiceReviewItemDto
            {
                ReviewId = ProtectReviewId(r.UserId),
                ShortRef = ComputeShortRef(r.UserId),
                SubmittedAtUtc = r.SubmittedAtUtc,
                Status = UserStatus.Pending.ToString(),
                HasVoice = _fileUrls.HasFile(r.VoiceVerificationPath)
            }).ToList();

            return Paginated(items, totalCount, pageNumber, pageSize);
        }

        public async Task<Response<PrivateFileUrlDto>> GetVoiceUrlAsync(string reviewId)
        {
            if (!TryResolveUserId(reviewId, out var userId))
                return NotFound<PrivateFileUrlDto>("Voice review item not found or expired.");

            var user = await _userManager.FindByIdAsync(userId.ToString());
            if (user == null || user.Status != UserStatus.Pending)
                return NotFound<PrivateFileUrlDto>("Voice review item not found.");

            var targetRoles = (await _userManager.GetRolesAsync(user)) ?? Array.Empty<string>();
            if (AppRoles.AnyProtectedFromModerators(targetRoles))
                return NotFound<PrivateFileUrlDto>("Voice review item not found.");

            var issued = (await _fileUrls.IssueAsync(
                PrivateFileKind.Voice,
                new[] { new PrivateFileRef(user.Id, user.VoiceVerificationPath, user.Id) },
                "VoiceReview.VoiceUrl"))[0];
            if (issued == null)
                return NotFound<PrivateFileUrlDto>("Voice recording not found.");

            return Success(new PrivateFileUrlDto { Url = issued.Url, ExpiresAtUtc = issued.ExpiresAtUtc });
        }

        public async Task<VoiceReviewDecisionResponse> DecideAsync(
            string reviewId,
            VoiceReviewDecisionDto model,
            Guid actorUserId)
        {
            if (model == null || model.Decision is not (UserStatus.Active or UserStatus.ReRecord or UserStatus.Rejected))
                return VoiceReviewDecisionResponse.From(BadRequest<string>("Decision must be Active, ReRecord, or Rejected."));

            if (!TryValidateDecisionReason(model.Decision, model.Reason, out var normalizedReason, out var reasonError))
                return VoiceReviewDecisionResponse.From(BadRequest<string>(reasonError));

            if (!TryResolveUserId(reviewId, out var userId))
                return VoiceReviewDecisionResponse.From(NotFound<string>("Voice review item not found or expired."));

            if (userId == actorUserId)
                return VoiceReviewDecisionResponse.From(BadRequest<string>("You cannot review your own account."));

            var user = await _userManager.FindByIdAsync(userId.ToString());
            if (user == null)
                return VoiceReviewDecisionResponse.From(NotFound<string>("Voice review item not found."));

            var targetRoles = (await _userManager.GetRolesAsync(user)) ?? Array.Empty<string>();
            if (AppRoles.AnyProtectedFromModerators(targetRoles))
                return VoiceReviewDecisionResponse.From(Forbidden<string>("Voice review decisions cannot be applied to protected accounts."));

            if (user.Status != UserStatus.Pending)
                return VoiceReviewDecisionResponse.From(Conflict<string>("This voice review item is no longer pending."));

            var transition = await _adminService.ApplyStatusTransitionAsync(
                user,
                model.Decision,
                targetRoles,
                actorUserId,
                isBulk: false);
            if (!transition.Succeeded)
            {
                var errorResponse = transition.IsConcurrencyConflict
                    ? Conflict<string>("This voice review item is no longer pending.")
                    : BadRequest<string>(transition.ErrorMessage ?? "Failed to apply voice review decision.");
                return VoiceReviewDecisionResponse.From(errorResponse);
            }

            var shortRef = ComputeShortRef(user.Id);
            await _auditLog.RecordAsync(new AuditEntry(
                AuditActions.VoiceReviewDecision,
                actorUserId,
                user.Id,
                user.Email,
                new
                {
                    fromStatus = transition.OldStatus.ToString(),
                    toStatus = transition.NewStatus.ToString(),
                    shortRef
                })
            {
                Category = AuditCategories.VoiceReview,
                EntityType = AuditEntityTypes.User,
                EntityId = user.Id.ToString(),
                FromState = transition.OldStatus.ToString(),
                ToState = transition.NewStatus.ToString(),
                Reason = normalizedReason
            });

            return VoiceReviewDecisionResponse.From(
                Success($"Voice review decision '{model.Decision}' applied."),
                transition.ShouldRevokeSession ? user.Id : null);
        }

        private static bool TryValidateDecisionReason(
            UserStatus decision,
            string? reason,
            out string? normalizedReason,
            out string errorMessage)
        {
            if (decision is UserStatus.ReRecord or UserStatus.Rejected)
            {
                var ok = AuditReasonValidator.TryValidateRequired(reason, out var required, out errorMessage);
                normalizedReason = ok ? required : null;
                return ok;
            }

            return AuditReasonValidator.TryValidateOptional(reason, out normalizedReason, out errorMessage);
        }

        private string ProtectReviewId(Guid userId) =>
            _protector.Protect(userId.ToString("D"), ReviewRefLifetime);

        private bool TryResolveUserId(string? reviewId, out Guid userId)
        {
            userId = Guid.Empty;
            if (string.IsNullOrWhiteSpace(reviewId))
                return false;

            try
            {
                var unprotected = _protector.Unprotect(reviewId);
                return Guid.TryParse(unprotected, out userId) && userId != Guid.Empty;
            }
            catch (CryptographicException)
            {
                return false;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private string ComputeShortRef(Guid userId)
        {
            var payload = Encoding.UTF8.GetBytes(userId.ToString("D"));
            var hash = HMACSHA256.HashData(_shortRefKey, payload);
            return "VR-" + Convert.ToHexString(hash, 0, 4);
        }
    }
}
