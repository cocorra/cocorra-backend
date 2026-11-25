using System;

namespace Cocorra.DAL.DTOS.VoiceReviewDto
{
    public class VoiceReviewItemDto
    {
        public string ReviewId { get; set; } = string.Empty;
        public string ShortRef { get; set; } = string.Empty;
        public DateTime SubmittedAtUtc { get; set; }
        public string Status { get; set; } = string.Empty;
        public bool HasVoice { get; set; }
    }
}
