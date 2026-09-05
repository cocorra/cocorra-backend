using System.ComponentModel.DataAnnotations.Schema;

namespace Cocorra.DAL.Models;

public class RoomParticipant
{
    // --- الربط (Composite Key) ---
    public Guid RoomId { get; set; }
    [ForeignKey(nameof(RoomId))]
    public virtual Room? Room { get; set; }

    public Guid UserId { get; set; }
    [ForeignKey(nameof(UserId))]
    public virtual ApplicationUser? User { get; set; }

    // --- الحالة العامة ---
    public ParticipantStatus Status { get; set; } = ParticipantStatus.Active;

    // ضفنا دي بدل CreatedAt اللي كانت في BaseEntity
    /// <summary>
    /// AN-031: the FIRST time this user entered the room. Previously overwritten on every
    /// rejoin, which destroyed the original join time and made session length unmeasurable
    /// for anyone who reconnected — precisely the users with a poor connection.
    /// </summary>
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;

    /// <summary>AN-031: most recent (re)entry, so rejoin behaviour is visible.</summary>
    public DateTime? LastJoinedAt { get; set; }

    /// <summary>
    /// AN-031: when the participant left. Null while present. Without it, time-in-room cannot
    /// be computed at all — only that someone joined.
    /// </summary>
    public DateTime? LeftAt { get; set; }

    /// <summary>Number of times this participant re-entered after leaving.</summary>
    public int RejoinCount { get; set; }

    // --- حالة الاستيدج والمايك ---
    public bool IsOnStage { get; set; } = false;
    public bool IsHandRaised { get; set; } = false;
    public bool IsMuted { get; set; } = true;

    // --- منطق حساب الوقت ---
    public double TotalSpokenSeconds { get; set; } = 0;
    public DateTime? LastUnmutedAt { get; set; }
    public int ExtraMinutesGranted { get; set; } = 0;
}