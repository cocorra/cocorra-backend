using System;

namespace Cocorra.DAL.DTOS.ChatDto
{
    public class MessageDto
    {
        public Guid Id { get; set; }
        public Guid SenderId { get; set; }
        public Guid ReceiverId { get; set; }
        public string Content { get; set; } = string.Empty;
        public bool IsRead { get; set; }
        public DateTime CreatedAt { get; set; }
        public Guid? OriginRoomId { get; set; }
        public Guid? OriginTextRoomId { get; set; }
    }
}