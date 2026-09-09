using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

using Cocorra.DAL.Enums;

namespace Cocorra.DAL.Models;

[Index(nameof(HostId), nameof(Status))]
public class Room : BaseEntity
{
    [Required(ErrorMessage = "Room title is required")]
    [MaxLength(100)]
    public string RoomTitle { get; set; } = string.Empty;

    [MaxLength(250)]
    public string? Description { get; set; }

    public DateTime StartDate { get; set; } = DateTime.UtcNow;

    [System.ComponentModel.DataAnnotations.Schema.Column("status")]
    public RoomStatus Status { get; set; } = RoomStatus.Scheduled;

    // --- إعدادات السعة (Capacity Settings) ---

    [Range(2, 1000, ErrorMessage = "Total capacity must be between 2 and 1000")]
    public int TotalCapacity { get; set; } = 50; // عدد الجمهور + الاستيدج

    [Range(1, 20, ErrorMessage = "Stage capacity must be between 1 and 20")]
    public int StageCapacity { get; set; } = 5; // عدد الكراسي اللي عالمنصة

    // --- إعدادات الوقت (Time Settings) ---

    // الوقت الافتراضي لأي حد يطلع الاستيدج (بالدقائق)
    [Range(1, 60)]
    public int DefaultSpeakerDurationMinutes { get; set; } = 5;

    // --- إعدادات النظام (Logic Settings) ---

    // هل النظام أوتوماتيك ولا الكوتش بيختار؟
    public RoomSelectionMode SelectionMode { get; set; } = RoomSelectionMode.Manual_CoachDecision;

    public Guid HostId { get; set; } 
    [ForeignKey(nameof(HostId))]
    public virtual ApplicationUser? Host { get; set; }
    public virtual ICollection<RoomParticipant> Participants { get; set; } = new List<RoomParticipant>();
    public bool IsPrivate { get; set; } = false;

    public string? ImagePath { get; set; }
    public int DurationHours { get; set; } = 2;

    [Required]
    public RoomCategory Category { get; set; }

    /// <summary>
    /// When the host's connection dropped while the room was Live, or null if the host is
    /// connected. A dropped socket no longer ends the room: the session is held open until
    /// this is older than the configured grace window, at which point HostReconnectGraceService
    /// ends it. Cleared when the host rejoins.
    ///
    /// Persisted rather than held in memory because RoomHub's connection map is static and
    /// per-process — it does not survive a restart and is wrong across multiple instances,
    /// so an in-memory timer would silently strand rooms on deploy.
    /// </summary>
    public DateTime? HostDisconnectedAt { get; set; }
}