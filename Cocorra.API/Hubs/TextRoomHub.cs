using System;
using System.Threading.Tasks;
using Cocorra.API.Authorization;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.ChatService;
using Cocorra.BLL.Services.TextRooms;
using Cocorra.DAL.DTOS.ChatDto;
using Cocorra.DAL.DTOS.TextRoomDto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Cocorra.API.Hubs
{
    [Authorize]
    public sealed class TextRoomHub : Hub
    {
        public const string StaffMonitorGroup = "text-room-staff";

        public static string RoomGroup(Guid roomId) => $"text-room:{roomId}";

        private readonly ITextRoomService _textRoomService;
        private readonly ITextRoomModerationService _moderationService;
        private readonly IDirectMessageNotifier _dmNotifier;

        public TextRoomHub(
            ITextRoomService textRoomService,
            ITextRoomModerationService moderationService,
            IDirectMessageNotifier dmNotifier)
        {
            _textRoomService = textRoomService;
            _moderationService = moderationService;
            _dmNotifier = dmNotifier;
        }

        public async Task<TextRoomJoinResultDto> JoinTextRoom(Guid roomId)
        {
            var outcome = await _textRoomService.JoinAsync(
                roomId, RequireUserId(), Context.User.IsTextRoomStaff(), Context.ConnectionId);

            if (outcome.Status is TextRoomJoinStatus.Joined or TextRoomJoinStatus.Redirected
                && outcome.RoomId is { } joinedRoomId)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, RoomGroup(joinedRoomId));
            }

            return outcome;
        }

        public async Task LeaveTextRoom(Guid roomId)
        {
            await _textRoomService.LeaveAsync(roomId, RequireUserId(), Context.ConnectionId);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, RoomGroup(roomId));
        }

        public Task<SendTextRoomMessageResultDto> SendTextRoomMessage(Guid roomId, string content) =>
            _textRoomService.SendMessageAsync(roomId, RequireUserId(), content, Context.User.IsTextRoomStaff());

        public async Task<Response<MessageDto>> SendTextRoomPrivateMessage(
            Guid roomId, Guid targetUserId, string content)
        {
            var response = await _textRoomService.SendPrivateMessageAsync(roomId, RequireUserId(), targetUserId, content);

            if (response.Succeeded && response.Data is not null)
                await _dmNotifier.DeliverAsync(response.Data);

            return response;
        }

        public async Task JoinStaffMonitor()
        {
            RequireStaffActor();
            await Groups.AddToGroupAsync(Context.ConnectionId, StaffMonitorGroup);
        }

        public Task<Response<string>> DeleteTextRoomMessage(long messageId, string reason) =>
            _moderationService.DeleteMessageAsync(messageId, reason, RequireStaffActor());

        public Task<Response<string>> MuteTextRoomUser(Guid roomId, Guid userId, int minutes, string reason) =>
            _moderationService.MuteUserAsync(roomId, userId, minutes, reason, RequireStaffActor());

        public Task<Response<string>> KickTextRoomUser(Guid roomId, Guid userId, string reason) =>
            _moderationService.KickUserAsync(roomId, userId, reason, RequireStaffActor());

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            await _textRoomService.DisconnectAsync(Context.ConnectionId);
            await base.OnDisconnectedAsync(exception);
        }

        private Guid RequireUserId()
        {
            if (!Context.User.TryGetUserId(out var userId))
                throw new HubException("Unauthorized");

            return userId;
        }

        private StaffActor RequireStaffActor()
        {
            if (!Context.User.IsTextRoomStaff())
                throw new HubException("Forbidden");

            return Context.User.ToStaffActor() ?? throw new HubException("Forbidden");
        }
    }
}
