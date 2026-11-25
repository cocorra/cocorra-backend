using System.Text.Json.Serialization;
using Cocorra.BLL.Base;
using Cocorra.DAL.Enums;

namespace Cocorra.BLL.Services.AdminService
{
    public sealed class StatusChangeResponse : Response<string>
    {
        [JsonIgnore]
        public bool SessionRevoked { get; init; }

        public static StatusChangeResponse From(Response<string> response, bool sessionRevoked = false) => new()
        {
            StatusCode = response.StatusCode,
            Meta = response.Meta,
            Succeeded = response.Succeeded,
            Message = response.Message,
            Errors = response.Errors,
            Data = response.Data,
            SessionRevoked = sessionRevoked
        };
    }

    public sealed record StatusTransitionOutcome(
        bool Succeeded,
        UserStatus OldStatus,
        UserStatus NewStatus,
        bool ShouldRevokeSession,
        string? ErrorMessage = null,
        bool IsConcurrencyConflict = false);
}
