using System;

namespace Cocorra.DAL.DTOS.ChatDto
{
    public class ChatFriendDto
    {
        public Guid FriendId { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string ProfilePicturePath { get; set; } = string.Empty;
        public string LastMessage { get; set; } = string.Empty;
        public DateTime? LastMessageDate { get; set; }
        public int UnreadCount { get; set; }
        /// <summary>
        /// The list holds every conversation partner, not only friends. Clients must not drop
        /// entries where this is false: those are chats started from a room.
        /// </summary>
        public bool IsFriend { get; set; }
        /// <summary>The audio or text room the conversation started in, when it started in one.</summary>
        public Guid? StartedInRoomId { get; set; }
        public Guid? StartedInTextRoomId { get; set; }
    }
}