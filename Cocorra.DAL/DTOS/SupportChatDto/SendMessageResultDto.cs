using System.Text.Json.Serialization;

namespace Cocorra.DAL.DTOS.SupportChatDto
{
    public class SendMessageResultDto
    {
        public Guid ChatId { get; set; }
        public SupportMessageDto Message { get; set; } = new();
        public bool IsNewChat { get; set; }

        // Internal routing metadata for SignalR delivery; staff IDs are never exposed to the user.
        [JsonIgnore]
        public string? ClaimedByAdminId { get; set; }
    }
}
