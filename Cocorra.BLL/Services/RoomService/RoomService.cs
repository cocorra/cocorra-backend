using Cocorra.BLL.Events;
using Cocorra.BLL.Services.LiveKit;
using Cocorra.BLL.Services.Upload;
using Cocorra.DAL.DTOS.RoomDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.RoomRepository;
using Cocorra.BLL.Base;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Cocorra.BLL.Services.NotificationService;
using Microsoft.AspNetCore.Identity;

namespace Cocorra.BLL.Services.RoomService;

public class RoomService : ResponseHandler, IRoomService
{
    private readonly IRoomRepository _roomRepo;
    private readonly IMediator _mediator;
    private readonly IUploadImage _uploadImage;
    private readonly string _baseUrl;
    private readonly IPushNotificationService _pushService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILiveKitService _liveKitService;
    private readonly LiveKitSettings _liveKitSettings;

    private static readonly HashSet<int> AllowedDurations = new() { 2, 3 };

    public RoomService(
        IRoomRepository roomRepo, 
        IMediator mediator, 
        IUploadImage uploadImage, 
        IConfiguration configuration,
        IPushNotificationService pushService,
        UserManager<ApplicationUser> userManager,
        ILiveKitService liveKitService,
        IOptions<LiveKitSettings> liveKitSettings)
    {
        _roomRepo = roomRepo;
        _mediator = mediator;
        _uploadImage = uploadImage;
        _baseUrl = configuration["AppSettings:BaseUrl"]?.TrimEnd('/') ?? "";
        _pushService = pushService;
        _userManager = userManager;
        _liveKitService = liveKitService;
        _liveKitSettings = liveKitSettings.Value;
    }

    private string? BuildFullUrl(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;
            
        if (relativePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
            relativePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return relativePath;
        }
        
        return $"{_baseUrl}/{relativePath.Replace("\\", "/").TrimStart('/')}";
    }

    public async Task<Response<Guid>> CreateRoomAsync(CreateRoomDto dto, Guid hostId, IFormFile? roomImage = null)
    {
        try
        {
            // Validate duration
            if (!AllowedDurations.Contains(dto.DurationHours))
            {
                return BadRequest<Guid>("Room duration must be exactly 2 or 3 hours.");
            }

            var status = RoomStatus.Live;
            if (dto.ScheduledStartDate.HasValue && dto.ScheduledStartDate > DateTime.UtcNow)
            {
                status = RoomStatus.Scheduled;
            }

            // Handle room image upload
            string? imagePath = null;
            if (roomImage != null && roomImage.Length > 0)
            {
                var savedPath = await _uploadImage.SaveImageAsync(roomImage);
                if (!savedPath.StartsWith("Error"))
                {
                    // Relocate from Profiles subfolder to Rooms subfolder
                    imagePath = savedPath.Replace("Uploads/img/Profiles/", "Uploads/img/Rooms/");
                    // Ensure the Rooms directory exists and move the file
                    var profilesPath = Path.Combine(GetContentPath(), savedPath.Replace("/", Path.DirectorySeparatorChar.ToString()));
                    var roomsDir = Path.Combine(GetContentPath(), "Uploads", "img", "Rooms");
                    if (!Directory.Exists(roomsDir)) Directory.CreateDirectory(roomsDir);
                    var fileName = Path.GetFileName(savedPath);
                    var roomsFilePath = Path.Combine(roomsDir, fileName);
                    if (File.Exists(profilesPath))
                    {
                        File.Move(profilesPath, roomsFilePath);
                    }
                }
            }

            var room = new Room
            {
                RoomTitle = dto.RoomTitle,
                Description = dto.Description,
                TotalCapacity = dto.TotalCapacity,
                StageCapacity = dto.StageCapacity,
                DefaultSpeakerDurationMinutes = dto.DefaultSpeakerDurationMinutes,
                IsPrivate = dto.IsPrivate,
                SelectionMode = dto.SelectionMode,
                HostId = hostId,
                StartDate = dto.ScheduledStartDate ?? DateTime.UtcNow,
                Status = status,
                CreatedAt = DateTime.UtcNow,
                ImagePath = imagePath,
                DurationHours = dto.DurationHours,
                Category = dto.Category
            };

            if (status == RoomStatus.Live)
            {
                var hostParticipant = new RoomParticipant
                {
                    UserId = hostId,
                    Status = ParticipantStatus.Active,
                    IsOnStage = true,
                    IsMuted = false,
                    JoinedAt = DateTime.UtcNow,
                    LastUnmutedAt = DateTime.UtcNow
                };
                room.Participants.Add(hostParticipant);
            }

            await _roomRepo.AddAsync(room);
            return Success(room.Id);
        }
        catch (Exception ex)
        {
            return BadRequest<Guid>($"Failed to create room: {ex.Message}");
        }
    }

    private string GetContentPath()
    {
        return Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
    }

    public async Task<Response<JoinRoomResultDto>> JoinRoomAsync(Guid roomId, Guid userId)
    {
        var room = await _roomRepo.GetByIdAsync(roomId);
        if (room == null) return NotFound<JoinRoomResultDto>("Room not found.");

        if (room.Status == RoomStatus.Scheduled)
        {
            return BadRequest<JoinRoomResultDto>("This room has not started yet. You can set a reminder instead.");
        }
        if (room.Status == RoomStatus.Ended || room.Status == RoomStatus.Cancelled)
        {
            return BadRequest<JoinRoomResultDto>("This room is no longer available.");
        }

        var allParticipants = await _roomRepo.GetRoomParticipantsAsync(roomId);
        var activeCount = allParticipants.Count(p => p.Status == ParticipantStatus.Active || p.Status == ParticipantStatus.PendingApproval);

        var existingParticipant = allParticipants.FirstOrDefault(p => p.UserId == userId);

        // Helper to resolve display name for token generation
        string ResolveName(RoomParticipant p) =>
            ((p.User?.FirstName ?? "") + " " + (p.User?.LastName ?? "")).Trim();

        if (existingParticipant != null)
        {
            if (existingParticipant.Status == ParticipantStatus.Active)
            {
                var name = ResolveName(existingParticipant);
                var token = _liveKitService.GenerateToken(roomId, userId, name);
                return Success(new JoinRoomResultDto
                {
                    LiveKitToken = token,
                    LiveKitServerUrl = _liveKitSettings.ServerUrl
                });
            }
            if (existingParticipant.Status == ParticipantStatus.Kicked) return BadRequest<JoinRoomResultDto>("You are banned from this room.");

            if (activeCount >= room.TotalCapacity) return BadRequest<JoinRoomResultDto>("Room is full.");

            existingParticipant.Status = room.IsPrivate ? ParticipantStatus.PendingApproval : ParticipantStatus.Active;
            existingParticipant.JoinedAt = DateTime.UtcNow;
            existingParticipant.IsOnStage = false;
            existingParticipant.IsMuted = true;

            await _roomRepo.UpdateParticipantAsync(existingParticipant);
            await _roomRepo.SaveChangesAsync();

            if (room.IsPrivate)
            {
                return Success(new JoinRoomResultDto(), message: "Request sent.");
            }
            else
            {
                var name = ResolveName(existingParticipant);
                var token = _liveKitService.GenerateToken(roomId, userId, name);
                return Success(new JoinRoomResultDto
                {
                    LiveKitToken = token,
                    LiveKitServerUrl = _liveKitSettings.ServerUrl
                }, message: "Rejoined successfully.");
            }
        }

        if (activeCount >= room.TotalCapacity)
        {
            return BadRequest<JoinRoomResultDto>("Room is full.");
        }

        var newParticipant = new RoomParticipant
        {
            RoomId = roomId,
            UserId = userId,
            JoinedAt = DateTime.UtcNow,
            IsOnStage = false,
            IsMuted = true,
            Status = room.IsPrivate ? ParticipantStatus.PendingApproval : ParticipantStatus.Active
        };
        if (room.IsPrivate)
        {
            var notification = new Notification
            {
                UserId = room.HostId, 
                Title = "New Join Request 🚪",
                Message = "Someone has requested to join your private room.",
                Type = NotificationType.RoomReminder, 
                ReferenceId = room.Id,
                CreatedAt = DateTime.UtcNow,
                IsRead = false
            };
            await _mediator.Publish(new UserRequestedToJoinRoomEvent(room.HostId, userId, roomId));
            await _roomRepo.AddNotificationsAsync(new List<Notification> { notification });

            var hostUser = await _userManager.FindByIdAsync(room.HostId.ToString());
            if (!string.IsNullOrEmpty(hostUser?.FcmToken))
            {
                var data = new Dictionary<string, string> { { "type", "room" }, { "roomId", room.Id.ToString() } };
                try { await _pushService.SendPushNotificationAsync(hostUser.FcmToken, notification.Title, notification.Message, data); } catch { }
            }
        }

        await _roomRepo.AddParticipantAsync(newParticipant);
        await _roomRepo.SaveChangesAsync();

        if (room.IsPrivate)
        {
            return Success(new JoinRoomResultDto(), message: "Request sent, waiting for approval.");
        }
        else
        {
            // Fetch the user info for the display name
            var user = await _userManager.FindByIdAsync(userId.ToString());
            var displayName = ((user?.FirstName ?? "") + " " + (user?.LastName ?? "")).Trim();
            var token = _liveKitService.GenerateToken(roomId, userId, displayName);
            return Success(new JoinRoomResultDto
            {
                LiveKitToken = token,
                LiveKitServerUrl = _liveKitSettings.ServerUrl
            }, message: "Joined successfully.");
        }
    }
    public async Task<Response<bool>> ApproveUserAsync(Guid roomId, Guid targetUserId, Guid hostId)
    {
        var room = await _roomRepo.GetByIdAsync(roomId);
        if (room == null) return NotFound<bool>("Room not found.");

        if (room.HostId != hostId)
            return BadRequest<bool>("Only the host can approve join requests.");

        var participant = await _roomRepo.GetParticipantAsync(roomId, targetUserId);
        if (participant == null)
            return NotFound<bool>("User request not found.");

        if (participant.Status == ParticipantStatus.Active)
            return Success(true, "User is already active in the room.");

        participant.Status = ParticipantStatus.Active;
        await _roomRepo.UpdateParticipantAsync(participant);

        var notification = new Notification
        {
            UserId = targetUserId,
            Title = "Request Approved ✅",
            Message = $"Your request to join the room '{room.RoomTitle}' has been approved! You can enter now.",
            Type = NotificationType.RoomReminder, 
            ReferenceId = room.Id,
            CreatedAt = DateTime.UtcNow,
            IsRead = false
        };
        await _roomRepo.AddNotificationsAsync(new List<Notification> { notification });

        var approvedUser = await _userManager.FindByIdAsync(targetUserId.ToString());
        if (!string.IsNullOrEmpty(approvedUser?.FcmToken))
        {
            var data = new Dictionary<string, string> { { "type", "room" }, { "roomId", room.Id.ToString() } };
            try { await _pushService.SendPushNotificationAsync(approvedUser.FcmToken, notification.Title, notification.Message, data); } catch { }
        }

        await _roomRepo.SaveChangesAsync();
        await _mediator.Publish(new UserApprovedToJoinRoomEvent(targetUserId, roomId));
        return Success(true, "User approved successfully.");
    }
    public async Task<Response<RoomStateDto>> GetRoomStateAsync(Guid roomId, Guid currentUserId)
    {
        var room = await _roomRepo.GetByIdAsync(roomId);
        if (room == null) return NotFound<RoomStateDto>("Room not found.");

        var currentParticipant = await _roomRepo.GetParticipantAsync(roomId, currentUserId);
        if (currentParticipant == null || currentParticipant.Status != ParticipantStatus.Active)
        {
            return BadRequest<RoomStateDto>("You are not an active member of this room.");
        }

        var participants = await _roomRepo.GetRoomParticipantsAsync(roomId);
        var activeParticipants = participants.Where(p => p.Status == ParticipantStatus.Active).ToList();

        // Generate a fresh LiveKit token for the requesting user
        var currentName = ((currentParticipant.User?.FirstName ?? "") + " " + (currentParticipant.User?.LastName ?? "")).Trim();
        var liveKitToken = _liveKitService.GenerateToken(roomId, currentUserId, currentName);

        var roomState = new RoomStateDto
        {
            RoomId = room.Id,
            RoomTitle = room.RoomTitle,
            HostId = room.HostId,
            TotalCapacity = room.TotalCapacity,
            StageCapacity = room.StageCapacity,
            Category = room.Category,
            CategoryName = room.Category.ToString(),
            Participants = activeParticipants.Select(p => new ParticipantStateDto
            {
                UserId = p.UserId,
                Name = p.User?.FirstName + " " + p.User?.LastName,
                ProfilePicture = BuildFullUrl(p.User?.ProfilePicturePath),
                IsOnStage = p.IsOnStage,
                IsMuted = p.IsMuted,
                IsHandRaised = p.IsHandRaised,
                JoinedAt = p.JoinedAt
            }).ToList(),
            LiveKitToken = liveKitToken,
            LiveKitServerUrl = _liveKitSettings.ServerUrl
        };

        return Success(roomState);
    }

    public async Task<Response<IEnumerable<RoomSummaryDto>>> GetRoomsFeedAsync(Guid currentUserId, RoomCategory? categoryId = null, int pageNumber = 1, int pageSize = 20)
    {
        var activeRooms = await _roomRepo.GetActiveRoomsAsync(categoryId, pageNumber, pageSize);
        if (!activeRooms.Any())
            return Success<IEnumerable<RoomSummaryDto>>(Enumerable.Empty<RoomSummaryDto>());

        var roomIds = activeRooms.Select(r => r.Id).ToList();

        var liveRoomIds = activeRooms.Where(r => r.Status == RoomStatus.Live).Select(r => r.Id).ToList();
        var scheduledRoomIds = activeRooms.Where(r => r.Status == RoomStatus.Scheduled).Select(r => r.Id).ToList();

        var participantCounts = new Dictionary<Guid, int>();
        if (liveRoomIds.Any())
        {
            var allParticipants = await _roomRepo.GetTableNoTracking()
                .SelectMany(r => r.Participants)
                .Where(p => liveRoomIds.Contains(p.RoomId) && p.Status == ParticipantStatus.Active)
                .GroupBy(p => p.RoomId)
                .Select(g => new { RoomId = g.Key, Count = g.Count() })
                .ToListAsync();

            foreach (var p in allParticipants)
                participantCounts[p.RoomId] = p.Count;
        }

        var reminderCounts = new Dictionary<Guid, int>();
        var userReminders = new HashSet<Guid>();
        if (scheduledRoomIds.Any())
        {
            foreach (var roomId in scheduledRoomIds)
            {
                reminderCounts[roomId] = await _roomRepo.GetRoomRemindersCountAsync(roomId);
                var reminder = await _roomRepo.GetRoomReminderAsync(roomId, currentUserId);
                if (reminder != null) userReminders.Add(roomId);
            }
        }

        var resultList = activeRooms.Select(room => new RoomSummaryDto
        {
            Id = room.Id,
            RoomTitle = room.RoomTitle,
            Description = room.Description,
            Status = room.Status,
            ScheduledStartDate = room.StartDate,
            DurationHours = room.DurationHours,
            Category = room.Category,
            CategoryName = room.Category.ToString(),
            HostId = room.HostId,
            HostName = room.Host != null ? $"{room.Host.FirstName} {room.Host.LastName}" : "Unknown",
            HostProfilePicture = room.Host != null ? BuildFullUrl(room.Host.ProfilePicturePath) : null,
            RoomImage = BuildFullUrl(room.ImagePath),
            ListenersCount = room.Status == RoomStatus.Live
                ? (participantCounts.TryGetValue(room.Id, out var c) ? c : 0)
                : (reminderCounts.TryGetValue(room.Id, out var rc) ? rc : 0),
            IsReminderSetByMe = userReminders.Contains(room.Id)
        }).ToList();

        return Success<IEnumerable<RoomSummaryDto>>(resultList);
    }

    public async Task<Response<string>> StartScheduledRoomAsync(Guid roomId, Guid hostId)
    {
        var room = await _roomRepo.GetByIdAsync(roomId);
        if (room == null) return NotFound<string>("Room not found.");

        if (room.HostId != hostId)
            return BadRequest<string>("Only the host can start this room.");

        if (room.Status == RoomStatus.Live)
            return BadRequest<string>("This room is already live.");

        if (room.Status == RoomStatus.Ended || room.Status == RoomStatus.Cancelled)
            return BadRequest<string>("This room is no longer available.");

        room.Status = RoomStatus.Live;
        await _roomRepo.UpdateAsync(room); 

        var hostParticipant = new RoomParticipant
        {
            RoomId = roomId,
            UserId = hostId,
            Status = ParticipantStatus.Active,
            IsOnStage = true,
            IsMuted = false,
            JoinedAt = DateTime.UtcNow,
            LastUnmutedAt = DateTime.UtcNow
        };
        await _roomRepo.AddParticipantAsync(hostParticipant);

        var reminders = await _roomRepo.GetRemindersByRoomIdAsync(roomId);
        if (reminders.Any())
        {
            var notifications = reminders.Select(r => new Notification
            {
                UserId = r.UserId,
                Title = "Room Starting Now! 🎙️",
                Message = $"The room '{room.RoomTitle}' has just started. Join now!",
                Type = NotificationType.RoomReminder,
                ReferenceId = room.Id,
                IsRead = false,
                CreatedAt = DateTime.UtcNow
            }).ToList();

            await _roomRepo.AddNotificationsAsync(notifications);
            
            var data = new Dictionary<string, string> { { "type", "room" }, { "roomId", room.Id.ToString() } };
            foreach (var r in reminders)
            {
                var user = await _userManager.FindByIdAsync(r.UserId.ToString());
                if (!string.IsNullOrEmpty(user?.FcmToken))
                {
                    try { await _pushService.SendPushNotificationAsync(user.FcmToken, "Room Starting Now! 🎙️", $"The room '{room.RoomTitle}' has just started. Join now!", data); } catch { }
                }
            }

            await _roomRepo.RemoveRemindersAsync(reminders);
        }

        await _roomRepo.SaveChangesAsync();

        return Success("Room is now live and notifications have been sent!");
    }
    public async Task<Response<string>> ToggleReminderAsync(Guid roomId, Guid userId)
    {
        var room = await _roomRepo.GetByIdAsync(roomId);
        if (room == null) return NotFound<string>("Room not found.");

        if (room.Status != RoomStatus.Scheduled)
            return BadRequest<string>("You can only set reminders for scheduled rooms.");

        var existingReminder = await _roomRepo.GetRoomReminderAsync(roomId, userId);

        if (existingReminder != null)
        {
            await _roomRepo.RemoveRoomReminderAsync(existingReminder);
            return Success("Reminder removed.");
        }
        else
        {
            var reminder = new RoomReminder
            {
                RoomId = roomId,
                UserId = userId,
                CreatedAt = DateTime.UtcNow
            };
            await _roomRepo.AddRoomReminderAsync(reminder);
            return Success("Reminder set successfully.");
        }
    }

    public async Task<Response<string>> EndRoomAsync(Guid roomId, Guid hostId)
    {
        var room = await _roomRepo.GetByIdAsync(roomId);
        if (room == null) return NotFound<string>("Room not found.");

        if (room.HostId != hostId)
            return BadRequest<string>("Only the host can end this room.");

        if (room.Status == RoomStatus.Ended || room.Status == RoomStatus.Cancelled)
            return BadRequest<string>("This room has already ended.");

        room.Status = RoomStatus.Ended;
        await _roomRepo.UpdateAsync(room);

        var participants = await _roomRepo.GetRoomParticipantsAsync(roomId);
        foreach (var p in participants.Where(p => p.Status == ParticipantStatus.Active || p.Status == ParticipantStatus.PendingApproval))
        {
            if (!p.IsMuted && p.LastUnmutedAt.HasValue)
            {
                p.TotalSpokenSeconds += (DateTime.UtcNow - p.LastUnmutedAt.Value).TotalSeconds;
                p.LastUnmutedAt = null;
            }
            p.Status = ParticipantStatus.Left;
            p.IsOnStage = false;
            p.IsMuted = true;
            p.IsHandRaised = false;
            await _roomRepo.UpdateParticipantAsync(p);
        }

        await _roomRepo.SaveChangesAsync();
        return Success("Room has been ended successfully.");
    }

    public async Task LeaveRoomCleanupAsync(Guid roomId, Guid userId)
    {
        var participant = await _roomRepo.GetParticipantAsync(roomId, userId);
        if (participant == null || participant.Status != ParticipantStatus.Active) return;

        if (!participant.IsMuted && participant.LastUnmutedAt.HasValue)
        {
            participant.TotalSpokenSeconds += (DateTime.UtcNow - participant.LastUnmutedAt.Value).TotalSeconds;
            participant.LastUnmutedAt = null;
        }

        participant.Status = ParticipantStatus.Left;
        participant.IsOnStage = false;
        participant.IsMuted = true;
        participant.IsHandRaised = false;

        await _roomRepo.UpdateParticipantAsync(participant);
        await _roomRepo.SaveChangesAsync();
    }

    public async Task<Response<IEnumerable<RoomSummaryDto>>> GetEndedRoomsHistoryAsync(int pageNumber = 1, int pageSize = 20)
    {
        var endedRooms = await _roomRepo.GetEndedRoomsAsync(pageNumber, pageSize);
        if (!endedRooms.Any())
            return Success<IEnumerable<RoomSummaryDto>>(Enumerable.Empty<RoomSummaryDto>());

        var resultList = endedRooms.Select(room => new RoomSummaryDto
        {
            Id = room.Id,
            RoomTitle = room.RoomTitle,
            Description = room.Description,
            Status = room.Status,
            ScheduledStartDate = room.StartDate,
            DurationHours = room.DurationHours,
            Category = room.Category,
            CategoryName = room.Category.ToString(),
            HostId = room.HostId,
            HostName = room.Host != null ? $"{room.Host.FirstName} {room.Host.LastName}" : "Unknown",
            HostProfilePicture = room.Host != null ? BuildFullUrl(room.Host.ProfilePicturePath) : null,
            RoomImage = BuildFullUrl(room.ImagePath),
            ListenersCount = room.Participants.Count,
            IsReminderSetByMe = false
        }).ToList();

        return Success<IEnumerable<RoomSummaryDto>>(resultList);
    }
}