using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Cocorra.DAL.Enums;

namespace Cocorra.DAL.DTOS.AdminDto
{
    public class SendBroadcastNotificationDto
    {
        private string _body = string.Empty;

        [Required(ErrorMessage = "Title is required")]
        [MaxLength(100, ErrorMessage = "Title cannot exceed 100 characters")]
        public string Title { get; set; } = string.Empty;

        [MaxLength(500, ErrorMessage = "Body cannot exceed 500 characters")]
        public string Body
        {
            get => string.IsNullOrWhiteSpace(_body) ? Message : _body;
            set => _body = value;
        }

        public string Message
        {
            get => _body;
            set => _body = value;
        }

        public Dictionary<string, string>? Data { get; set; }

        /// <summary>
        /// Optional status filter (e.g. Active). If null, broadcasts to all non-banned, non-rejected users.
        /// </summary>
        public UserStatus? TargetStatus { get; set; }

        /// <summary>
        /// Whether to create in-app notification records in database. Default is true.
        /// </summary>
        public bool SaveInAppNotification { get; set; } = true;
    }
}
