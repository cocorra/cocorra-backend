using System;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.DAL.DTOS.StorageDto;
using Cocorra.DAL.DTOS.VoiceReviewDto;

namespace Cocorra.BLL.Services.VoiceReviewService
{
    public sealed class VoiceReviewDecisionResponse : Response<string>
    {
        [JsonIgnore]
        public Guid? RevokedUserId { get; init; }

        public static VoiceReviewDecisionResponse From(Response<string> response, Guid? revokedUserId = null) => new()
        {
            StatusCode = response.StatusCode,
            Meta = response.Meta,
            Succeeded = response.Succeeded,
            Message = response.Message,
            Errors = response.Errors,
            Data = response.Data,
            RevokedUserId = revokedUserId
        };
    }

    public interface IVoiceReviewService
    {
        Task<PagedResponse<VoiceReviewItemDto>> GetPendingAsync(int pageNumber = 1, int pageSize = 20);
        Task<Response<PrivateFileUrlDto>> GetVoiceUrlAsync(string reviewId);
        Task<VoiceReviewDecisionResponse> DecideAsync(string reviewId, VoiceReviewDecisionDto model, Guid actorUserId);
    }
}
