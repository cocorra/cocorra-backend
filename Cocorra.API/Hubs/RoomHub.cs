using Cocorra.BLL.Services.ChatService;
using Cocorra.BLL.Services.LiveKit;
using Cocorra.BLL.Services.RoomService;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Repository.RoomRepository;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Security.Claims;

namespace Cocorra.API.Hubs
{
    [Authorize]
    public class RoomHub : Hub
    {
        private readonly IRoomRepository _roomRepo;
        private readonly IRoomService _roomService;
        private readonly IChatService _chatService;
        private readonly ILiveKitService _liveKitService;
        private readonly LiveKitSettings _liveKitSettings;

        // Thread-safe mapping: ConnectionId → (UserId, RoomId)
        private static readonly ConcurrentDictionary<string, (Guid UserId, Guid RoomId)> _connections = new();

        public RoomHub(IRoomRepository roomRepo, IRoomService roomService, IChatService chatService, ILiveKitService liveKitService, IOptions<LiveKitSettings> liveKitSettings)
        {
            _roomRepo = roomRepo;
            _roomService = roomService;
            _chatService = chatService;
            _liveKitService = liveKitService;
            _liveKitSettings = liveKitSettings.Value;
        }

        public override async Task OnConnectedAsync()
        {
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            if (_connections.TryRemove(Context.ConnectionId, out var mapping))
            {
                try
                {
                    // Check if this user is the host — if so, end the room entirely
                    var room = await _roomRepo.GetByIdAsync(mapping.RoomId);
                    if (room != null && room.HostId == mapping.UserId && room.Status == RoomStatus.Live)
                    {
                        // Host disconnected — end the room for everyone
                        await _roomService.EndRoomAsync(mapping.RoomId, mapping.UserId);

                        var roomIdStr = mapping.RoomId.ToString();
                        await Clients.Group(roomIdStr).SendAsync("RoomEnded", new
                        {
                            RoomId = mapping.RoomId,
                            Message = "The host has disconnected. This room has been ended."
                        });

                        // Purge all connections for this room
                        PurgeRoomConnections(mapping.RoomId);
                    }
                    else
                    {
                        // Regular participant disconnect
                        await _roomService.LeaveRoomCleanupAsync(mapping.RoomId, mapping.UserId);

                        var roomIdString = mapping.RoomId.ToString();
                        await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomIdString);
                        await Clients.Group(roomIdString).SendAsync("UserLeft", new
                        {
                            UserId = mapping.UserId
                        });
                    }
                }
                catch { }
            }

            await base.OnDisconnectedAsync(exception);
        }

        private Guid GetUserId()
        {
            var userIdString = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdString) || !Guid.TryParse(userIdString, out Guid userId))
                throw new HubException("Unauthorized user.");
            return userId;
        }

        private static Guid ParseGuidSafe(string value, string fieldName)
        {
            if (!Guid.TryParse(value, out Guid result))
                throw new HubException($"Invalid {fieldName}.");
            return result;
        }

        /// <summary>
        /// Removes all _connections entries that belong to a specific room.
        /// Called when a room ends to prevent stale OnDisconnectedAsync cleanup.
        /// </summary>
        private static void PurgeRoomConnections(Guid roomId)
        {
            var connectionIds = _connections
                .Where(kvp => kvp.Value.RoomId == roomId)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var connId in connectionIds)
                _connections.TryRemove(connId, out _);
        }

        /// <summary>
        /// Returns all active SignalR connection IDs for a given user.
        /// Used by AdminController to force-disconnect banned users from active rooms.
        /// </summary>
        public static IReadOnlyList<string> GetConnectionsForUser(Guid userId)
        {
            return _connections
                .Where(kvp => kvp.Value.UserId == userId)
                .Select(kvp => kvp.Key)
                .ToList();
        }

        /// <summary>
        /// Removes a user's entries from the connection tracking dictionary.
        /// Called after force-aborting their connections on ban.
        /// </summary>
        public static void PurgeUserConnections(Guid userId)
        {
            var connectionIds = _connections
                .Where(kvp => kvp.Value.UserId == userId)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var connId in connectionIds)
                _connections.TryRemove(connId, out _);
        }

        public async Task JoinRoom(string roomId)
        {
            var userId = GetUserId();
            var roomGuid = ParseGuidSafe(roomId, "Room ID");

            var room = await _roomRepo.GetByIdAsync(roomGuid);
            if (room == null || room.Status != RoomStatus.Live)
                throw new HubException("Room is not live yet or has ended.");

            var participant = await _roomRepo.GetParticipantAsync(roomGuid, userId);

            if (participant == null)
                throw new HubException("You are not a member of this room. Please join via the REST API first.");
            if (participant.Status == ParticipantStatus.PendingApproval)
                throw new HubException("Your request is still pending approval from the host.");
            if (participant.Status == ParticipantStatus.Kicked || participant.Status == ParticipantStatus.Rejected)
                throw new HubException("You are not allowed to join this room.");

            // Re-activate users who had previously left (e.g., disconnect/reconnect)
            if (participant.Status == ParticipantStatus.Left)
            {
                participant.Status = ParticipantStatus.Active;
                participant.JoinedAt = DateTime.UtcNow;
                participant.IsOnStage = false;
                participant.IsMuted = true;
                participant.IsHandRaised = false;
                await _roomRepo.UpdateParticipantAsync(participant);
                await _roomRepo.SaveChangesAsync();
            }

            // If this user already has an old connection tracked, remove it first
            var existingConnId = _connections
                .FirstOrDefault(kvp => kvp.Value.UserId == userId && kvp.Value.RoomId == roomGuid).Key;
            if (existingConnId != null && existingConnId != Context.ConnectionId)
            {
                _connections.TryRemove(existingConnId, out _);
                await Groups.RemoveFromGroupAsync(existingConnId, roomId);
            }

            // Track connection for disconnect cleanup
            _connections[Context.ConnectionId] = (userId, roomGuid);

            await Groups.AddToGroupAsync(Context.ConnectionId, roomId);

            await Clients.Group(roomId).SendAsync("UserJoined", new
            {
                UserId = userId,
                Name = participant.User?.FirstName + " " + participant.User?.LastName,
                IsOnStage = participant.IsOnStage
            });

            // Send LiveKit token to the joining user so they can connect to the media server
            var displayName = ((participant.User?.FirstName ?? "") + " " + (participant.User?.LastName ?? "")).Trim();
            var liveKitToken = _liveKitService.GenerateToken(roomGuid, userId, displayName);
            await Clients.Caller.SendAsync("LiveKitToken", new
            {
                Token = liveKitToken,
                ServerUrl = _liveKitSettings.ServerUrl
            });
        }

        public async Task LeaveRoom(string roomId)
        {
            var userId = GetUserId();
            var roomGuid = ParseGuidSafe(roomId, "Room ID");

            await _roomService.LeaveRoomCleanupAsync(roomGuid, userId);

            _connections.TryRemove(Context.ConnectionId, out _);

            await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);
            await Clients.Group(roomId).SendAsync("UserLeft", new
            {
                UserId = userId
            });
        }

        public async Task RaiseHand(string roomId)
        {
            var userId = GetUserId();
            var roomGuid = ParseGuidSafe(roomId, "Room ID");

            var participant = await _roomRepo.GetParticipantAsync(roomGuid, userId);
            if (participant == null) throw new HubException("You are not a member of this room.");

            if (participant.IsOnStage) return;

            participant.IsHandRaised = true;
            await _roomRepo.UpdateParticipantAsync(participant);
            await _roomRepo.SaveChangesAsync();

            await Clients.Group(roomId).SendAsync("HandRaised", new
            {
                UserId = userId,
                Name = participant.User?.FirstName + " " + participant.User?.LastName
            });
        }

        public async Task LowerHand(string roomId)
        {
            var userId = GetUserId();
            var roomGuid = ParseGuidSafe(roomId, "Room ID");

            var participant = await _roomRepo.GetParticipantAsync(roomGuid, userId);
            if (participant == null) throw new HubException("You are not a member of this room.");

            participant.IsHandRaised = false;
            await _roomRepo.UpdateParticipantAsync(participant);
            await _roomRepo.SaveChangesAsync();

            await Clients.Group(roomId).SendAsync("HandLowered", new
            {
                UserId = userId,
                Name = participant.User?.FirstName + " " + participant.User?.LastName
            });
        }

        public async Task ApproveToStage(string roomId, string targetUserId)
        {
            var hostId = GetUserId();
            var roomGuid = ParseGuidSafe(roomId, "Room ID");
            var targetGuid = ParseGuidSafe(targetUserId, "Target User ID");

            var room = await _roomRepo.GetByIdAsync(roomGuid);
            if (room == null || room.HostId != hostId)
                throw new HubException("Only the host can approve speakers to the stage.");

            var stageSpeakers = await _roomRepo.GetStageSpeakersAsync(roomGuid);
            if (stageSpeakers.Count >= room.StageCapacity)
                throw new HubException("Stage is full. Someone must leave the stage first.");

            var participant = await _roomRepo.GetParticipantAsync(roomGuid, targetGuid);
            if (participant == null) throw new HubException("User not found in room.");

            participant.IsOnStage = true;
            participant.IsHandRaised = false;
            participant.IsMuted = true; // Start muted on stage, user unmutes when ready

            await _roomRepo.UpdateParticipantAsync(participant);
            await _roomRepo.SaveChangesAsync();

            await Clients.Group(roomId).SendAsync("StageUpdated", new
            {
                UserId = targetGuid,
                IsOnStage = true,
                Name = participant.User?.FirstName + " " + participant.User?.LastName
            });
        }

        public async Task MoveToAudience(string roomId, string targetUserId)
        {
            var hostId = GetUserId();
            var roomGuid = ParseGuidSafe(roomId, "Room ID");
            var targetGuid = ParseGuidSafe(targetUserId, "Target User ID");

            var room = await _roomRepo.GetByIdAsync(roomGuid);
            if (room == null || room.HostId != hostId)
                throw new HubException("Only the host can demote speakers.");

            var participant = await _roomRepo.GetParticipantAsync(roomGuid, targetGuid);
            if (participant == null) return;

            if (!participant.IsMuted && participant.LastUnmutedAt.HasValue)
            {
                var spokenSeconds = (DateTime.UtcNow - participant.LastUnmutedAt.Value).TotalSeconds;
                participant.TotalSpokenSeconds += spokenSeconds;
                participant.LastUnmutedAt = null;
            }

            participant.IsOnStage = false;
            participant.IsMuted = true;
            participant.IsHandRaised = false; // Clear stale hand-raise flag

            await _roomRepo.UpdateParticipantAsync(participant);
            await _roomRepo.SaveChangesAsync();

            await Clients.Group(roomId).SendAsync("StageUpdated", new
            {
                UserId = targetGuid,
                IsOnStage = false,
                Name = participant.User?.FirstName + " " + participant.User?.LastName
            });

            await Clients.Group(roomId).SendAsync("MicStatusChanged", new
            {
                UserId = targetGuid,
                IsMuted = true,
                Name = participant.User?.FirstName
            });
        }

        public async Task ToggleMic(string roomId, bool muteStatus)
        {
            var userId = GetUserId();
            var roomGuid = ParseGuidSafe(roomId, "Room ID");

            var room = await _roomRepo.GetByIdAsync(roomGuid);
            if (room == null) return;

            var participant = await _roomRepo.GetParticipantAsync(roomGuid, userId);
            if (participant == null || !participant.IsOnStage) return;

            var totalAllowedSeconds = (room.DefaultSpeakerDurationMinutes + participant.ExtraMinutesGranted) * 60;
            var remainingSeconds = totalAllowedSeconds - participant.TotalSpokenSeconds;

            if (muteStatus == false && remainingSeconds <= 0 && userId != room.HostId)
            {
                throw new HubException("Your time is up! The host needs to grant you more time.");
            }

            if (muteStatus == false && participant.IsMuted == true)
            {
                participant.LastUnmutedAt = DateTime.UtcNow;
            }
            else if (muteStatus == true && participant.IsMuted == false)
            {
                if (participant.LastUnmutedAt.HasValue)
                {
                    var spokenSeconds = (DateTime.UtcNow - participant.LastUnmutedAt.Value).TotalSeconds;
                    participant.TotalSpokenSeconds += spokenSeconds;
                    participant.LastUnmutedAt = null;

                    remainingSeconds = totalAllowedSeconds - participant.TotalSpokenSeconds;
                }
            }

            participant.IsMuted = muteStatus;

            await _roomRepo.UpdateParticipantAsync(participant);
            await _roomRepo.SaveChangesAsync();

            await Clients.Group(roomId).SendAsync("MicStatusChanged", new
            {
                UserId = userId,
                IsMuted = muteStatus,
                Name = participant.User?.FirstName,
                RemainingSeconds = Math.Max(0, Math.Round(remainingSeconds))
            });
        }

        public async Task GrantExtraTime(string roomId, string targetUserId, int minutes)
        {
            var hostId = GetUserId();
            var roomGuid = ParseGuidSafe(roomId, "Room ID");
            var targetGuid = ParseGuidSafe(targetUserId, "Target User ID");

            var room = await _roomRepo.GetByIdAsync(roomGuid);
            if (room == null || room.HostId != hostId)
                throw new HubException("Only the host can grant extra time.");

            if (minutes < 1 || minutes > 30)
                throw new HubException("Extra time must be between 1 and 30 minutes.");

            var participant = await _roomRepo.GetParticipantAsync(roomGuid, targetGuid);
            if (participant == null || !participant.IsOnStage) return;

            participant.ExtraMinutesGranted += minutes;

            await _roomRepo.UpdateParticipantAsync(participant);
            await _roomRepo.SaveChangesAsync();

            await Clients.Group(roomId).SendAsync("ExtraTimeGranted", new
            {
                UserId = targetGuid,
                AddedMinutes = minutes,
                Name = participant.User?.FirstName
            });
        }

        public async Task KickUser(string roomId, string targetUserId)
        {
            var hostId = GetUserId();
            var roomGuid = ParseGuidSafe(roomId, "Room ID");
            var targetGuid = ParseGuidSafe(targetUserId, "Target User ID");

            if (hostId == targetGuid)
                throw new HubException("The host cannot kick themselves.");

            var room = await _roomRepo.GetByIdAsync(roomGuid);
            if (room == null || room.HostId != hostId)
                throw new HubException("Only the host can kick users.");

            var participant = await _roomRepo.GetParticipantAsync(roomGuid, targetGuid);
            if (participant == null) return;

            // Finalize spoken time if they were unmuted on stage
            if (!participant.IsMuted && participant.LastUnmutedAt.HasValue)
            {
                participant.TotalSpokenSeconds += (DateTime.UtcNow - participant.LastUnmutedAt.Value).TotalSeconds;
                participant.LastUnmutedAt = null;
            }

            participant.Status = ParticipantStatus.Kicked;
            participant.IsOnStage = false;
            participant.IsMuted = true;
            participant.IsHandRaised = false;

            await _roomRepo.UpdateParticipantAsync(participant);
            await _roomRepo.SaveChangesAsync();

            // Remove kicked user's connection from the group and purge tracking
            var kickedConnId = _connections
                .FirstOrDefault(kvp => kvp.Value.UserId == targetGuid && kvp.Value.RoomId == roomGuid).Key;
            if (kickedConnId != null)
            {
                _connections.TryRemove(kickedConnId, out _);
                await Groups.RemoveFromGroupAsync(kickedConnId, roomId);
            }

            await Clients.Group(roomId).SendAsync("UserKicked", new
            {
                UserId = targetGuid,
                Name = participant.User?.FirstName + " " + participant.User?.LastName
            });
        }

        public async Task EndRoom(string roomId)
        {
            var hostId = GetUserId();
            var roomGuid = ParseGuidSafe(roomId, "Room ID");

            var result = await _roomService.EndRoomAsync(roomGuid, hostId);

            if (!result.Succeeded)
                throw new HubException(result.Message);

            await Clients.Group(roomId).SendAsync("RoomEnded", new
            {
                RoomId = roomGuid,
                Message = "The host has ended this room."
            });

            // Purge all stale connection mappings for this room
            PurgeRoomConnections(roomGuid);
        }

        // ============================================================
        // Room Chat: Group (Ephemeral) & Private (Persistent)
        // ============================================================

        /// <summary>
        /// Sends an ephemeral group message to all participants in the room.
        /// NOT persisted to the database. Does NOT check UserBlock — all room
        /// members see group messages regardless of block status.
        /// </summary>
        public async Task SendRoomGroupMessage(string roomId, string content)
        {
            try
            {
                var userId = GetUserId();
                var roomGuid = ParseGuidSafe(roomId, "Room ID");

                if (string.IsNullOrWhiteSpace(content))
                {
                    await Clients.Caller.SendAsync("SendMessageError", new { Error = "Message cannot be empty." });
                    return;
                }

                // Verify the sender is an active participant in this room
                var participant = await _roomRepo.GetParticipantAsync(roomGuid, userId);
                if (participant == null || participant.Status != ParticipantStatus.Active)
                {
                    await Clients.Caller.SendAsync("SendMessageError", new { Error = "You are not an active member of this room." });
                    return;
                }

                var senderName = (participant.User?.FirstName + " " + participant.User?.LastName).Trim();

                await Clients.Group(roomId).SendAsync("ReceiveRoomMessage", new
                {
                    SenderId = userId,
                    SenderName = string.IsNullOrEmpty(senderName) ? "Unknown" : senderName,
                    ProfilePicturePath = participant.User?.ProfilePicturePath ?? "",
                    Content = content.Trim(),
                    Timestamp = DateTime.UtcNow
                });
            }
            catch (HubException)
            {
                throw; // Re-throw auth/parse errors from GetUserId/ParseGuidSafe
            }
            catch (Exception)
            {
                await Clients.Caller.SendAsync("SendMessageError", new { Error = "An unexpected error occurred. Please try again." });
            }
        }

        /// <summary>
        /// Sends a persistent private message to a specific user from within a room.
        /// Saved to the database via ChatService. ENFORCES the UserBlock system — if
        /// either party has blocked the other, the message is rejected.
        /// </summary>
        public async Task SendRoomPrivateMessage(Guid targetUserId, string content)
        {
            try
            {
                var userId = GetUserId();

                if (string.IsNullOrWhiteSpace(content))
                {
                    await Clients.Caller.SendAsync("SendMessageError", new { Error = "Message cannot be empty." });
                    return;
                }

                var result = await _chatService.SaveMessageAsync(userId, targetUserId, content);

                if (!result.Succeeded)
                {
                    await Clients.Caller.SendAsync("SendMessageError", new { Error = result.Message });
                    return;
                }

                var messageDto = result.Data;

                await Clients.User(targetUserId.ToString()).SendAsync("ReceivePrivateMessage", messageDto);
                await Clients.Caller.SendAsync("PrivateMessageSent", messageDto);
            }
            catch (HubException)
            {
                throw; // Re-throw auth errors from GetUserId
            }
            catch (Exception)
            {
                await Clients.Caller.SendAsync("SendMessageError", new { Error = "An unexpected error occurred. Please try again." });
            }
        }
    }
}