using Cocorra.DAL.Enums;

namespace Cocorra.DAL.DTOS.VoiceReviewDto
{
    public class VoiceReviewDecisionDto
    {
        public UserStatus Decision { get; set; }
        public string? Reason { get; set; }
    }
}
