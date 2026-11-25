using System;
using System.Collections.Generic;
using System.Text;

namespace Cocorra.DAL.DTOS.AdminDto
{
    public class UserDto
    {
        public string Id { get; set; } = default!;
        public string FullName { get; set; } = default!;
        public string? Email { get; set; }
        public int? Age { get; set; }
        public string? MBTI { get; set; }
        public string Status { get; set; } = default!;
        public DateTime CreatedAt { get; set; }
        // Always null in user responses: a voice recording is private, and a link to it is only
        // issued (and audit-logged) by GET Admin/User/{id}/VoiceUrl. Kept for response shape.
        public string? VoicePath { get; set; }
        /// <summary>True when the user has a voice recording to fetch through VoiceUrl.</summary>
        public bool HasVoice { get; set; }
        public IList<string> Roles { get; set; } = new List<string>();
    }
}
