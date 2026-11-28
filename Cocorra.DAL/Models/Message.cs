using Microsoft.EntityFrameworkCore;
using System;
using System.ComponentModel.DataAnnotations;

namespace Cocorra.DAL.Models
{
    [Index(nameof(ReceiverId), nameof(SenderId), nameof(CreatedAt))]
    public class Message : BaseEntity
    {
        public Guid SenderId { get; set; }
        public ApplicationUser? Sender { get; set; }

        public Guid ReceiverId { get; set; }
        public ApplicationUser? Receiver { get; set; }

        [Required]
        [MaxLength(1000)] // حد أقصى لطول الرسالة
        public string Content { get; set; } = string.Empty;

        public bool IsRead { get; set; } = false;

        /// <summary>The audio room the message was sent from, when it was sent from one.</summary>
        public Guid? OriginRoomId { get; set; }

        /// <summary>The text room the message was sent from, when it was sent from one.</summary>
        public Guid? OriginTextRoomId { get; set; }
    }
}