using Cocorra.BLL.Services.NotificationService; 
using Cocorra.DAL.DTOS.FriendDto;
using Cocorra.DAL.DTOS.NotificationDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.FriendRepository;
using Cocorra.DAL.Repository.NotificationRepository;
using Cocorra.BLL.Base;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.BLL.Services.EventTracking;

namespace Cocorra.BLL.Services.FriendService
{
    public class FriendService : ResponseHandler, IFriendService 
    {
        private readonly IFriendRepository _friendRepo;
        private readonly INotificationRepository _notificationRepo;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IPushNotificationService _pushService; 
        private readonly IEventTracker _eventTracker;

        public FriendService(IFriendRepository friendRepo, INotificationRepository notificationRepo, UserManager<ApplicationUser> userManager, IPushNotificationService pushService, IEventTracker eventTracker)
        {
            _friendRepo = friendRepo;
            _notificationRepo = notificationRepo;
            _userManager = userManager;
            _pushService = pushService;
            _eventTracker = eventTracker;
        }

        public async Task<Response<UserSearchDto>> SearchUserByIdAsync(Guid currentUserId, Guid targetUserId)
        {
            var targetUser = await _userManager.FindByIdAsync(targetUserId.ToString());
            if (targetUser == null) return NotFound<UserSearchDto>("User not found.");

            var friendRequest = await _friendRepo.GetFriendshipRelationAsync(currentUserId, targetUserId);

            string status = "None";
            if (friendRequest != null)
            {
                if (friendRequest.Status == FriendRequestStatus.Accepted) status = "Friends";
                else if (friendRequest.Status == FriendRequestStatus.Pending)
                {
                    status = friendRequest.SenderId == currentUserId ? "RequestSent" : "RequestReceived";
                }
            }

            var dto = new UserSearchDto
            {
                Id = targetUser.Id,
                FullName = $"{targetUser.FirstName} {targetUser.LastName}",
                FriendshipStatus = status
            };

            return Success(dto);
        }

        public async Task<Response<string>> SendFriendRequestAsync(Guid currentUserId, Guid targetUserId)
        {
            if (currentUserId == targetUserId)
                return BadRequest<string>("You cannot send a friend request to yourself.");

            var targetUser = await _userManager.FindByIdAsync(targetUserId.ToString());
            if (targetUser == null) return NotFound<string>("Target user not found.");

            var currentUser = await _userManager.FindByIdAsync(currentUserId.ToString());

            var existingRequest = await _friendRepo.GetFriendshipRelationAsync(currentUserId, targetUserId);

            if (existingRequest != null)
            {
                if (existingRequest.Status == FriendRequestStatus.Pending)
                    return BadRequest<string>("A friend request is already pending between you two.");
                if (existingRequest.Status == FriendRequestStatus.Accepted)
                    return BadRequest<string>("You are already friends.");
            }

            using var transaction = _friendRepo.BeginTransaction();
            try
            {
                FriendRequest request;

                if (existingRequest != null && existingRequest.Status == FriendRequestStatus.Rejected)
                {
                    existingRequest.SenderId = currentUserId;
                    existingRequest.ReceiverId = targetUserId;
                    existingRequest.Status = FriendRequestStatus.Pending;
                    existingRequest.CreatedAt = DateTime.UtcNow;
                    await _friendRepo.UpdateAsync(existingRequest);
                    request = existingRequest;
                }
                else
                {
                    request = new FriendRequest
                    {
                        SenderId = currentUserId,
                        ReceiverId = targetUserId,
                        Status = FriendRequestStatus.Pending,
                        CreatedAt = DateTime.UtcNow
                    };
                    await _friendRepo.AddAsync(request);
                }

                var notification = new Notification
                {
                    UserId = targetUserId,
                    Title = "New Friend Request",
                    Message = $"{currentUser?.FirstName} sent you a friend request.",
                    Type = NotificationType.FriendRequest,
                    ReferenceId = request.Id,
                    CreatedAt = DateTime.UtcNow,
                    IsRead = false
                };
                await _notificationRepo.AddAsync(notification);

                transaction.Commit();

                _eventTracker.Track(EventTypes.FriendRequestSent, currentUserId, new { targetUserId });

                try 
                {
                    if (!string.IsNullOrEmpty(targetUser?.FcmToken))
                        await _pushService.SendPushNotificationAsync(targetUser.FcmToken, "New Friend Request", notification.Message, new Dictionary<string, string> { { "type", "general" } }); 
                } 
                catch { }

                return Success("Friend request sent successfully.");
            }
            catch (DbUpdateException)
            {
                transaction.Rollback();
                return BadRequest<string>("A request is already in progress.");
            }
            catch (Exception)
            {
                transaction.Rollback();
                return BadRequest<string>("An internal error occurred while processing the request.");
            }
        }

        public async Task<Response<string>> RespondToFriendRequestAsync(Guid currentUserId, Guid senderId, bool accept)
        {
            var request = await _friendRepo.GetPendingRequestAsync(senderId, currentUserId);

            if (request == null)
                return BadRequest<string>("Friend request not found or already processed.");

            using var transaction = _friendRepo.BeginTransaction();
            try
            {
                request.Status = accept ? FriendRequestStatus.Accepted : FriendRequestStatus.Rejected;
                await _friendRepo.UpdateAsync(request);

                if (accept)
                {
                    var currentUser = await _userManager.FindByIdAsync(currentUserId.ToString());
                    var notification = new Notification
                    {
                        UserId = senderId,
                        Title = "Friend Request Accepted",
                        Message = $"{currentUser?.FirstName} accepted your friend request.",
                        Type = NotificationType.FriendAccept,
                        ReferenceId = request.Id,
                        CreatedAt = DateTime.UtcNow,
                        IsRead = false
                    };
                    await _notificationRepo.AddAsync(notification);
                    transaction.Commit();

                    _eventTracker.Track(EventTypes.FriendRequestAccepted, currentUserId, new { senderId });

                    try 
                    { 
                        var senderUser = await _userManager.FindByIdAsync(senderId.ToString());
                        if (!string.IsNullOrEmpty(senderUser?.FcmToken))
                            await _pushService.SendPushNotificationAsync(senderUser.FcmToken, "Friend Request Accepted", notification.Message, new Dictionary<string, string> { { "type", "general" } }); 
                    } 
                    catch { }
                }
                else
                {
                    transaction.Commit();
                }

                return Success(accept ? "Friend request accepted." : "Friend request rejected.");
            }
            catch
            {
                transaction.Rollback();
                return BadRequest<string>("Failed to respond to friend request.");
            }
        }

        public async Task<Response<string>> RemoveFriendOrCancelRequestAsync(Guid currentUserId, Guid targetUserId)
        {
            var existingRequest = await _friendRepo.GetFriendshipRelationAsync(currentUserId, targetUserId);

            if (existingRequest == null)
                return BadRequest<string>("No friendship or pending request found between you two.");

            if (existingRequest.Status == FriendRequestStatus.Pending)
            {
                var relatedNotification = await _notificationRepo.GetTableAsTracking()
                    .FirstOrDefaultAsync(n => n.ReferenceId == existingRequest.Id && n.Type == NotificationType.FriendRequest);

                if (relatedNotification != null)
                {
                    await _notificationRepo.DeleteAsync(relatedNotification);
                }
            }
            await _friendRepo.DeleteAsync(existingRequest);

            return Success("Action completed successfully.");
        }
    }
}