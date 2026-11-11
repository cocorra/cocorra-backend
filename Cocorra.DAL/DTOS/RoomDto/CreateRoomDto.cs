using System.ComponentModel.DataAnnotations;
using Cocorra.DAL.Enums;

namespace Cocorra.DAL.DTOS.RoomDto;

public class CreateRoomDto
{
    [Required]
    [MaxLength(100)]
    public string RoomTitle { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>
    /// Planned start as an instant with an offset ("Z" or "+02:00"); offset-less values are
    /// rejected. Omitted or in the past, the room goes live immediately.
    /// </summary>
    public DateTime? ScheduledStartDate { get; set; }

    /// <summary>
    /// Room duration in hours. Only 2 or 3 are accepted.
    /// </summary>
    [Required]
    public int DurationHours { get; set; } = 2;

    public bool IsPrivate { get; set; } = false;
    public int TotalCapacity { get; set; } = 50;
    public int StageCapacity { get; set; } = 5;
    public int DefaultSpeakerDurationMinutes { get; set; } = 5;
    public RoomSelectionMode SelectionMode { get; set; } = RoomSelectionMode.Manual_CoachDecision;

    [Required]
    public RoomCategory Category { get; set; }
}