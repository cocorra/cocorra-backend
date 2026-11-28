using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.ChatDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.GenericRepository;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Text;

namespace Cocorra.DAL.Repository.MessageRepository
{
    public class MessageRepository : GenericRepositoryAsync<Message>, IMessageRepository
    {
        public MessageRepository(AppDbContext dbContext) : base(dbContext)
        {
        }
        public async Task<List<Message>> GetChatHistoryAsync(Guid userId1, Guid userId2, int pageNumber, int pageSize)
        {
            var query = _dbContext.Messages
                .Where(m => (m.SenderId == userId1 && m.ReceiverId == userId2) ||
                            (m.SenderId == userId2 && m.ReceiverId == userId1));

            var pagedMessages = await query
                .OrderByDescending(m => m.CreatedAt)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize) 
                .ToListAsync();

            return pagedMessages.OrderBy(m => m.CreatedAt).ToList();
        }
        public async Task<Message?> GetLastMessageAsync(Guid userId1, Guid userId2)
        {
            return await _dbContext.Messages
                .Where(m => (m.SenderId == userId1 && m.ReceiverId == userId2) ||
                            (m.SenderId == userId2 && m.ReceiverId == userId1))
                .OrderByDescending(m => m.CreatedAt) // بنرتب من الأحدث للأقدم
                .FirstOrDefaultAsync(); // بناخد أول واحدة بس
        }

        public async Task<int> GetUnreadCountAsync(Guid senderId, Guid receiverId)
        {
            // بنعد الرسايل اللي مبعوتالي أنا (receiverId) ومقرتهاش
            return await _dbContext.Messages
                .CountAsync(m => m.SenderId == senderId && m.ReceiverId == receiverId && !m.IsRead);
        }

        public async Task MarkMessagesAsReadAsync(Guid senderId, Guid receiverId)
        {
            await _dbContext.Messages
                .Where(m => m.SenderId == senderId && m.ReceiverId == receiverId && !m.IsRead)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsRead, true));
        }
        public async Task<List<ChatFriendDto>> GetRecentChatSummariesAsync(Guid currentUserId)
        {
            var blockedUserIds = await _dbContext.UserBlocks
                .Where(ub => ub.BlockerId == currentUserId || ub.BlockedId == currentUserId)
                .Select(ub => ub.BlockerId == currentUserId ? ub.BlockedId : ub.BlockerId)
                .Distinct()
                .ToListAsync();

            var conversations = await _dbContext.Messages
                .AsNoTracking()
                .Where(m => m.SenderId == currentUserId || m.ReceiverId == currentUserId)
                .Where(m => !blockedUserIds.Contains(m.SenderId == currentUserId ? m.ReceiverId : m.SenderId))
                .Select(m => new
                {
                    PartnerId = m.SenderId == currentUserId ? m.ReceiverId : m.SenderId,
                    Message = m
                })
                .GroupBy(x => x.PartnerId)
                .Select(g => new
                {
                    PartnerId = g.Key,
                    LastMessage = g.OrderByDescending(x => x.Message.CreatedAt).FirstOrDefault().Message,
                    UnreadCount = g.Count(x => x.Message.ReceiverId == currentUserId && !x.Message.IsRead),
                    StartedInRoomId = g.Where(x => x.Message.OriginRoomId != null)
                        .OrderBy(x => x.Message.CreatedAt)
                        .Select(x => x.Message.OriginRoomId)
                        .FirstOrDefault(),
                    StartedInTextRoomId = g.Where(x => x.Message.OriginTextRoomId != null)
                        .OrderBy(x => x.Message.CreatedAt)
                        .Select(x => x.Message.OriginTextRoomId)
                        .FirstOrDefault()
                })
                .ToListAsync();

            var partnerIds = conversations.Select(c => c.PartnerId).ToList();

            var friendIds = (await _dbContext.FriendRequests
                .AsNoTracking()
                .Where(f => f.Status == FriendRequestStatus.Accepted
                    && (f.SenderId == currentUserId || f.ReceiverId == currentUserId))
                .Select(f => f.SenderId == currentUserId ? f.ReceiverId : f.SenderId)
                .ToListAsync())
                .ToHashSet();
            var partners = await _dbContext.Users
                .AsNoTracking()
                .Where(u => partnerIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => new { u.FirstName, u.LastName, u.ProfilePicturePath });

            var dtoList = conversations.Select(c => 
            {
                var partnerInfo = partners.GetValueOrDefault(c.PartnerId);
                return new ChatFriendDto
                {
                    FriendId = c.PartnerId,
                    FullName = partnerInfo != null ? $"{partnerInfo.FirstName} {partnerInfo.LastName}" : "Unknown User",
                    ProfilePicturePath = partnerInfo?.ProfilePicturePath ?? "",
                    LastMessage = c.LastMessage?.Content ?? "",
                    LastMessageDate = c.LastMessage?.CreatedAt,
                    UnreadCount = c.UnreadCount,
                    IsFriend = friendIds.Contains(c.PartnerId),
                    StartedInRoomId = c.StartedInRoomId,
                    StartedInTextRoomId = c.StartedInTextRoomId
                };
            }).OrderByDescending(d => d.LastMessageDate ?? DateTime.MinValue).ToList();

            return dtoList;
        }
    }
}
