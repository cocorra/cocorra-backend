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

        private Task<List<Guid>> GetAdminRoleIdsAsync()
        {
            var normalizedAdmin = AppRoles.Admin.ToUpperInvariant();
            return _dbContext.Roles
                .AsNoTracking()
                .Where(r => r.Name == AppRoles.Admin || r.NormalizedName == normalizedAdmin)
                .Select(r => r.Id)
                .ToListAsync();
        }

        public async Task<(int TotalCount, List<Report> Items)> GetPagedReportsAsync(
            ReportCategory? category, string? status, int pageNumber, int pageSize, bool excludeAdmins = false)
        {
            var query = _dbContext.Reports.AsNoTracking().AsQueryable();

            if (excludeAdmins)
            {
                var adminRoleIds = await GetAdminRoleIdsAsync();
                if (adminRoleIds.Count > 0)
                {
                    query = query.Where(r =>
                        !_dbContext.UserRoles.Any(ur => ur.UserId == r.ReporterId && adminRoleIds.Contains(ur.RoleId))
                        && (r.ReportedUserId == null || !_dbContext.UserRoles.Any(ur => ur.UserId == r.ReportedUserId.Value && adminRoleIds.Contains(ur.RoleId))));
                }
            }

            if (category.HasValue)
            {
                query = query.Where(r => r.Category == category.Value);
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                query = query.Where(r => r.Status == status);
            }

            var totalCount = await query.CountAsync();

            var items = await query
                .Include(r => r.Reporter)
                .Include(r => r.ReportedUser)
                .Include(r => r.ReportedRoom)
                    .ThenInclude(room => room!.Host)
                .OrderByDescending(r => r.CreatedAt)
                .ThenByDescending(r => r.Id)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return (totalCount, items);
        }

        public async Task<Report?> GetReportByIdAsync(Guid reportId)
        {
            return await _dbContext.Reports.FindAsync(reportId);
        }

        public async Task<Report?> GetReportDetailsByIdAsync(Guid reportId)
        {
            return await _dbContext.Reports
                .AsNoTracking()
                .Include(r => r.Reporter)
                .Include(r => r.ReportedUser)
                .Include(r => r.ReportedRoom)
                    .ThenInclude(room => room!.Host)
                .FirstOrDefaultAsync(r => r.Id == reportId);
        }

        public async Task<SupportTicket?> GetTicketByIdAsync(Guid ticketId)
        {
            return await _dbContext.SupportTickets.FindAsync(ticketId);
        }

        public async Task<SupportTicket?> GetTicketDetailsByIdAsync(Guid ticketId)
        {
            return await _dbContext.SupportTickets
                .AsNoTracking()
                .Include(t => t.User)
                .FirstOrDefaultAsync(t => t.Id == ticketId);
        }

        public async Task<(int TotalCount, List<SupportTicket> Items)> GetPagedTicketsAsync(
            SupportTicketType? type, SupportTicketStatus? status, int pageNumber, int pageSize, bool excludeAdmins = false)
        {
            var query = _dbContext.SupportTickets.AsNoTracking().AsQueryable();

            if (excludeAdmins)
            {
                var adminRoleIds = await GetAdminRoleIdsAsync();
                if (adminRoleIds.Count > 0)
                {
                    query = query.Where(t =>
                        t.UserId == null || !_dbContext.UserRoles.Any(ur => ur.UserId == t.UserId.Value && adminRoleIds.Contains(ur.RoleId)));
                }
            }

            if (type.HasValue)
                query = query.Where(t => t.Type == type.Value);

            // Filters on the typed StatusCode: the string Status column is free-form.
            if (status.HasValue)
                query = query.Where(t => t.StatusCode == status.Value);

            var totalCount = await query.CountAsync();

            var items = await query
                .Include(t => t.User)
                .OrderByDescending(t => t.CreatedAt)
                .ThenByDescending(t => t.Id)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return (totalCount, items);
        }

        public async Task UpdateTicketAsync(SupportTicket ticket)
        {
            _dbContext.SupportTickets.Update(ticket);
            await _dbContext.SaveChangesAsync();
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

        public Task ReleaseActiveChatsForStaffAsync(string adminId) =>
            ReleaseActiveChatsForStaffAsync(_dbContext, adminId);

        public static async Task ReleaseActiveChatsForStaffAsync(AppDbContext dbContext, string adminId)
        {
            var activeChats = await dbContext.SupportChats
                .Where(c => c.AdminId == adminId && c.Status == SupportChatStatus.Active)
                .ToListAsync();

            if (activeChats.Count == 0)
                return;

            foreach (var chat in activeChats)
            {
                chat.AdminId = null;
                chat.Status = SupportChatStatus.Pending;
            }

            await dbContext.SaveChangesAsync();
        }

        public async Task<List<SupportChat>> GetPendingChatsAsync(int pageNumber, int pageSize)
        {
            return await _dbContext.SupportChats
                .Include(c => c.Messages)
                .Where(c => c.Status == SupportChatStatus.Pending && c.AdminId == null)
                .OrderByDescending(c => c.CreatedAt)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();
        }

        public async Task<int> GetPendingChatsCountAsync()
        {
            return await _dbContext.SupportChats
                .CountAsync(c => c.Status == SupportChatStatus.Pending && c.AdminId == null);
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
