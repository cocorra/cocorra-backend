using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.DAL.Data;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Microsoft.EntityFrameworkCore;

namespace Cocorra.DAL.Repository.SupportRepository
{
    public class SupportRepository : ISupportRepository
    {
        private readonly AppDbContext _dbContext;

        public SupportRepository(AppDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task AddTicketAsync(SupportTicket ticket)
        {
            await _dbContext.SupportTickets.AddAsync(ticket);
            await _dbContext.SaveChangesAsync();
        }

        public async Task AddReportAsync(Report report)
        {
            await _dbContext.Reports.AddAsync(report);
            await _dbContext.SaveChangesAsync();
        }

        public async Task<List<Report>> GetFilteredReportsAsync(ReportCategory? category, string? status)
        {
            var query = _dbContext.Reports
                .AsNoTracking()
                .Include(r => r.Reporter)
                .Include(r => r.ReportedUser)
                .Include(r => r.ReportedRoom)
                    .ThenInclude(room => room!.Host)
                .AsQueryable();

            if (category.HasValue)
            {
                query = query.Where(r => r.Category == category.Value);
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                query = query.Where(r => r.Status == status);
            }

            return await query
                .OrderByDescending(r => r.CreatedAt)
                .ToListAsync();
        }

        public async Task<Report?> GetReportByIdAsync(Guid reportId)
        {
            return await _dbContext.Reports.FindAsync(reportId);
        }

        public async Task<SupportTicket?> GetTicketByIdAsync(Guid ticketId)
        {
            return await _dbContext.SupportTickets.FindAsync(ticketId);
        }

        public async Task UpdateReportAsync(Report report)
        {
            _dbContext.Reports.Update(report);
            await _dbContext.SaveChangesAsync();
        }

        // Chat Support Methods
        public async Task<SupportChat?> GetUserOpenChatAsync(string userId)
        {
            return await _dbContext.SupportChats
                .Include(c => c.Messages)
                .Where(c => c.UserId == userId && (c.Status == SupportChatStatus.Pending || c.Status == SupportChatStatus.Active))
                .FirstOrDefaultAsync();
        }

        public async Task<SupportChat?> GetChatByIdAsync(Guid chatId)
        {
            return await _dbContext.SupportChats
                .Include(c => c.Messages)
                .FirstOrDefaultAsync(c => c.Id == chatId);
        }

        public async Task<int> GetPendingUserMessageCountAsync(Guid chatId)
        {
            return await _dbContext.SupportMessages
                .CountAsync(m => m.SupportChatId == chatId && !m.IsFromAdmin);
        }

        public async Task AddChatAsync(SupportChat chat)
        {
            await _dbContext.SupportChats.AddAsync(chat);
            await _dbContext.SaveChangesAsync();
        }

        public async Task AddMessageAsync(SupportMessage message)
        {
            await _dbContext.SupportMessages.AddAsync(message);
            await _dbContext.SaveChangesAsync();
        }

        public async Task UpdateChatAsync(SupportChat chat)
        {
            try
            {
                _dbContext.SupportChats.Update(chat);
                await _dbContext.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                throw; // Let the service layer handle it
            }
        }

        public async Task<List<SupportChat>> GetPendingChatsAsync(int pageNumber, int pageSize)
        {
            return await _dbContext.SupportChats
                .Include(c => c.Messages)
                .Where(c => c.Status == SupportChatStatus.Pending)
                .OrderByDescending(c => c.CreatedAt)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();
        }

        public async Task<int> GetPendingChatsCountAsync()
        {
            return await _dbContext.SupportChats
                .CountAsync(c => c.Status == SupportChatStatus.Pending);
        }

        public async Task<List<SupportChat>> GetAdminActiveChatsAsync(string adminId, int pageNumber, int pageSize)
        {
            return await _dbContext.SupportChats
                .Include(c => c.Messages)
                .Where(c => c.AdminId == adminId && c.Status == SupportChatStatus.Active)
                .OrderByDescending(c => c.CreatedAt)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();
        }

        public async Task<int> GetAdminActiveChatsCountAsync(string adminId)
        {
            return await _dbContext.SupportChats
                .CountAsync(c => c.AdminId == adminId && c.Status == SupportChatStatus.Active);
        }

        public async Task<List<SupportChat>> GetUserChatHistoryAsync(string userId, int pageNumber, int pageSize)
        {
            return await _dbContext.SupportChats
                .Include(c => c.Messages)
                .Where(c => c.UserId == userId && c.Status == SupportChatStatus.Closed)
                .OrderByDescending(c => c.ClosedAt)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();
        }

        public async Task<int> GetUserChatHistoryCountAsync(string userId)
        {
            return await _dbContext.SupportChats
                .CountAsync(c => c.UserId == userId && c.Status == SupportChatStatus.Closed);
        }
    }
}
