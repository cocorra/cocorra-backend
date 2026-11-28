using System.Collections.Generic;

namespace Cocorra.DAL.Models
{
    /// <summary>One entry of a rotation pool. Stored as JSON on its owning room or schedule.</summary>
    public class TextRoomTopic
    {
        public string Title { get; set; } = string.Empty;
        public string? Subject { get; set; }
        public string? Description { get; set; }
        public List<string> Prompts { get; set; } = new();
    }
}
