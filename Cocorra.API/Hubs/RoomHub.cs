using Cocorra.BLL.Services.ChatService;
using Cocorra.BLL.Services.LiveKit;
using Cocorra.BLL.Services.RoomService;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Repository.RoomRepository;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Security.Claims;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.DAL.Models;

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
        private readonly IEventTracker _eventTracker;
        private readonly ILogger<RoomHub> _logger;

        // Thread-safe mapping: ConnectionId → (UserId, RoomId)
        private static readonly ConcurrentDictionary<string, (Guid UserId, Guid RoomId)> _connections = new();

        public RoomHub(IRoomRepository roomRepo, IRoomService roomService, IChatService chatService, ILiveKitService liveKitService, IOptions<LiveKitSettings> liveKitSettings, IEventTracker eventTracker, ILogger<RoomHub> logger)
        {
            _roomRepo = roomRepo;
            _roomService = roomService;
            _chatService = chatService;
            _liveKitService = liveKitService;
            _liveKitSettings = liveKitSettings.Value;
            _eventTracker = eventTracker;
            _logger = logger;
        }

        // ==========================================================================
        // TEMPORARY DIAGNOSTIC LOGGING — "[JOINROOM-TRACE]" / "[HUB-TRACE]".
        // Remove once the LiveKitToken delivery investigation is closed.
        // Adds no control flow: every catch below rethrows, so behaviour is
        // byte-for-byte identical to before instrumentation.
        // ==========================================================================
        private static string Now() => DateTime.UtcNow.ToString("O");

        public override async Task OnConnectedAsync()
        {
            _logger.LogInformation(
                "[HUB-TRACE] OnConnectedAsync ConnectionId={ConnectionId} IsAuthenticated={IsAuthenticated} " +
                "UserIdClaim={UserIdClaim} UserIdentifier={UserIdentifier} Transport={Transport} Ts={Ts}",
                Context.ConnectionId,
                Context.User?.Identity?.IsAuthenticated,
                Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "(none)",
                Context.UserIdentifier ?? "(none)",
                Context.Features.Get<Microsoft.AspNetCore.Http.Connections.Features.IHttpTransportFeature>()?.TransportType.ToString() ?? "(unknown)",
                Now());

            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            _logger.LogInformation(exception,
                "[HUB-TRACE] OnDisconnectedAsync ConnectionId={ConnectionId} HadTrackedRoom={Tracked} " +
                "ExceptionType={ExceptionType} Ts={Ts}",
                Context.ConnectionId,
                _connections.ContainsKey(Context.ConnectionId),
                exception?.GetType().Name ?? "(none — clean close)",
                Now());

            if (_connections.TryRemove(Context.ConnectionId, out var mapping))
            {
                try
                {
                    _eventTracker.Track(EventTypes.RoomLeft, mapping.UserId, new { roomId = mapping.RoomId });
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
                catch (Exception ex)
                {
                    // TEMPORARY DIAGNOSTIC LOGGING — still swallowed, exactly as before.
                    _logger.LogError(ex,
                        "[HUB-TRACE] OnDisconnectedAsync cleanup threw (swallowed). " +
                        "ConnectionId={ConnectionId} UserId={UserId} RoomId={RoomId} Ts={Ts}",
                        Context.ConnectionId, mapping.UserId, mapping.RoomId, Now());
                }
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
            // [JOINROOM-TRACE] #1 — entering JoinRoom
            _logger.LogInformation(
                "[JOINROOM-TRACE] #1 ENTER JoinRoom ConnectionId={ConnectionId} RawRoomId={RawRoomId} " +
                "IsAuthenticated={IsAuthenticated} UserIdClaim={UserIdClaim} Ts={Ts}",
                Context.ConnectionId, roomId,
                Context.User?.Identity?.IsAuthenticated,
                Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "(none)",
                Now());

            try
            {
            var userId = GetUserId();
            var roomGuid = ParseGuidSafe(roomId, "Room ID");

            var room = await _roomRepo.GetByIdAsync(roomGuid);
            if (room == null || room.Status != RoomStatus.Live)
            {
                // [JOINROOM-TRACE] EXIT-A — throws before any token is generated
                _logger.LogWarning(
                    "[JOINROOM-TRACE] EXIT-A ABORT: room not live. ConnectionId={ConnectionId} UserId={UserId} " +
                    "RoomId={RoomId} RoomFound={RoomFound} Status={Status} Ts={Ts}",
                    Context.ConnectionId, userId, roomGuid, room != null,
                    room?.Status.ToString() ?? "(null)", Now());
                throw new HubException("Room is not live yet or has ended.");
            }

            var participant = await _roomRepo.GetParticipantAsync(roomGuid, userId);

            if (participant == null)
            {
                // [JOINROOM-TRACE] EXIT-B — throws before any token is generated
                _logger.LogWarning(
                    "[JOINROOM-TRACE] EXIT-B ABORT: no RoomParticipant row (client did not call POST /Room/{{id}}/Join first). " +
                    "ConnectionId={ConnectionId} UserId={UserId} RoomId={RoomId} Ts={Ts}",
                    Context.ConnectionId, userId, roomGuid, Now());
                throw new HubException("You are not a member of this room. Please join via the REST API first.");
            }
            if (participant.Status == ParticipantStatus.PendingApproval)
            {
                // [JOINROOM-TRACE] EXIT-C — throws before any token is generated
                _logger.LogWarning(
                    "[JOINROOM-TRACE] EXIT-C ABORT: participant PendingApproval. ConnectionId={ConnectionId} " +
                    "UserId={UserId} RoomId={RoomId} Ts={Ts}",
                    Context.ConnectionId, userId, roomGuid, Now());
                throw new HubException("Your request is still pending approval from the host.");
            }
            if (participant.Status == ParticipantStatus.Kicked || participant.Status == ParticipantStatus.Rejected)
            {
                // [JOINROOM-TRACE] EXIT-D — throws before any token is generated
                _logger.LogWarning(
                    "[JOINROOM-TRACE] EXIT-D ABORT: participant {Status}. ConnectionId={ConnectionId} " +
                    "UserId={UserId} RoomId={RoomId} Ts={Ts}",
                    participant.Status, Context.ConnectionId, userId, roomGuid, Now());
                throw new HubException("You are not allowed to join this room.");
            }

            _logger.LogInformation(
                "[JOINROOM-TRACE] #2 PRECHECKS PASSED ConnectionId={ConnectionId} UserId={UserId} RoomId={RoomId} " +
                "ParticipantStatus={Status} IsOnStage={IsOnStage} IsHost={IsHost} Ts={Ts}",
                Context.ConnectionId, userId, roomGuid, participant.Status, participant.IsOnStage,
                room.HostId == userId, Now());

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

            _eventTracker.Track(EventTypes.RoomJoined, userId, new { roomId = roomGuid });

            await Clients.Group(roomId).SendAsync("UserJoined", new
            {
                UserId = userId,
                Name = participant.User?.FirstName + " " + participant.User?.LastName,
                IsOnStage = participant.IsOnStage
            });

            // Send LiveKit token to the joining user so they can connect to the media server
            var displayName = ((participant.User?.FirstName ?? "") + " " + (participant.User?.LastName ?? "")).Trim();
            var canPublish = room.HostId == userId || participant.IsOnStage;

            // [JOINROOM-TRACE] #3 — before generating the LiveKit token
            _logger.LogInformation(
                "[JOINROOM-TRACE] #3 BEFORE GenerateToken ConnectionId={ConnectionId} UserId={UserId} " +
                "RoomId={RoomId} ParticipantName={ParticipantName} CanPublish={CanPublish} Ts={Ts}",
                Context.ConnectionId, userId, roomGuid, displayName, canPublish, Now());

            var liveKitToken = _liveKitService.GenerateToken(roomGuid, userId, displayName, canPublish);

            // [JOINROOM-TRACE] #4 — after generating the LiveKit token
            _logger.LogInformation(
                "[JOINROOM-TRACE] #4 AFTER GenerateToken ConnectionId={ConnectionId} UserId={UserId} " +
                "RoomId={RoomId} TokenLength={TokenLength} ServerUrl={ServerUrl} Ts={Ts}",
                Context.ConnectionId, userId, roomGuid, liveKitToken?.Length ?? -1,
                _liveKitSettings.ServerUrl, Now());

            // [JOINROOM-TRACE] #5 — immediately before SendAsync
            _logger.LogInformation(
                "[JOINROOM-TRACE] #5 BEFORE SendAsync(\"LiveKitToken\") ConnectionId={ConnectionId} UserId={UserId} " +
                "RoomId={RoomId} TokenLength={TokenLength} ServerUrl={ServerUrl} " +
                "ConnectionAborted={Aborted} Ts={Ts}",
                Context.ConnectionId, userId, roomGuid, liveKitToken?.Length ?? -1,
                _liveKitSettings.ServerUrl, Context.ConnectionAborted.IsCancellationRequested, Now());

            try
            {
                await Clients.Caller.SendAsync("LiveKitToken", new
                {
                    Token = liveKitToken,
                    ServerUrl = _liveKitSettings.ServerUrl,
                    IceServers = _liveKitSettings.IceServers
                });
            }
            catch (Exception sendEx)
            {
                // [JOINROOM-TRACE] #6-FAIL — SendAsync threw. Rethrown; behaviour unchanged.
                _logger.LogError(sendEx,
                    "[JOINROOM-TRACE] #6-FAIL SendAsync(\"LiveKitToken\") THREW {ExceptionType}. " +
                    "ConnectionId={ConnectionId} UserId={UserId} RoomId={RoomId} " +
                    "ConnectionAborted={Aborted} Ts={Ts}",
                    sendEx.GetType().Name, Context.ConnectionId, userId, roomGuid,
                    Context.ConnectionAborted.IsCancellationRequested, Now());
                throw;
            }

            // [JOINROOM-TRACE] #6 — SendAsync completed. NOTE: this proves the frame was handed to
            // the transport, NOT that the client processed it. Compare against the Flutter log.
            _logger.LogInformation(
                "[JOINROOM-TRACE] #6 AFTER SendAsync(\"LiveKitToken\") COMPLETED OK. " +
                "ConnectionId={ConnectionId} UserId={UserId} RoomId={RoomId} TokenLength={TokenLength} " +
                "ServerUrl={ServerUrl} ConnectionAborted={Aborted} Ts={Ts}",
                Context.ConnectionId, userId, roomGuid, liveKitToken?.Length ?? -1,
                _liveKitSettings.ServerUrl, Context.ConnectionAborted.IsCancellationRequested, Now());

            // [JOINROOM-TRACE] #7 — normal end of JoinRoom
            _logger.LogInformation(
                "[JOINROOM-TRACE] #7 EXIT-OK JoinRoom ConnectionId={ConnectionId} UserId={UserId} " +
                "RoomId={RoomId} Ts={Ts}",
                Context.ConnectionId, userId, roomGuid, Now());
            }
            catch (HubException hubEx)
            {
                // Expected client-facing aborts (EXIT-A..D and GetUserId/ParseGuidSafe). Rethrown unchanged.
                _logger.LogWarning(
                    "[JOINROOM-TRACE] EXIT-HUBEX JoinRoom threw HubException: {Message} " +
                    "ConnectionId={ConnectionId} RawRoomId={RawRoomId} Ts={Ts}",
                    hubEx.Message, Context.ConnectionId, roomId, Now());
                throw;
            }
            catch (Exception ex)
            {
                // Unexpected failure anywhere in JoinRoom. Rethrown unchanged — SignalR will send the
                // client a generic invocation error (EnableDetailedErrors is not set, Program.cs:122-128).
                _logger.LogError(ex,
                    "[JOINROOM-TRACE] EXIT-EX JoinRoom threw {ExceptionType} before completing. " +
                    "ConnectionId={ConnectionId} RawRoomId={RawRoomId} Ts={Ts}",
                    ex.GetType().Name, Context.ConnectionId, roomId, Now());
                throw;
            }
        }

        public async Task LeaveRoom(string roomId)
        {
            var userId = GetUserId();
            var roomGuid = ParseGuidSafe(roomId, "Room ID");

            await _roomService.LeaveRoomCleanupAsync(roomGuid, userId);

            _eventTracker.Track(EventTypes.RoomLeft, userId, new { roomId = roomGuid });

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

            try { await _liveKitService.UpdateStagePermissionAsync(roomGuid, targetGuid, canPublish: true); } catch { }

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

            try { await _liveKitService.UpdateStagePermissionAsync(roomGuid, targetGuid, canPublish: false); } catch { }

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
                _eventTracker.Track(EventTypes.MicActivated, userId, new { roomId = roomGuid });
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