using System;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.DAL.Data;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.SupportRepository;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cocorra.Tests;

public class SupportTicketRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;
    private readonly SupportRepository _repo;

    public SupportTicketRepositoryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        _repo = new SupportRepository(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<ApplicationUser> SeedUserAsync()
    {
        var id = Guid.NewGuid();
        var user = new ApplicationUser
        {
            Id = id,
            UserName = $"user-{id:N}",
            NormalizedUserName = $"USER-{id:N}".ToUpperInvariant(),
            Email = $"user-{id:N}@test.local",
            NormalizedEmail = $"USER-{id:N}@TEST.LOCAL",
            FirstName = "Test",
            LastName = "User",
            SecurityStamp = Guid.NewGuid().ToString()
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task GetPagedTicketsAsync_ReturnsTicketsNewestFirst_WithUserLoaded()
    {
        var user = await SeedUserAsync();
        var now = DateTime.UtcNow;
        _context.SupportTickets.AddRange(
            new SupportTicket { Id = Guid.NewGuid(), UserId = user.Id, Message = "older", CreatedAt = now.AddHours(-2) },
            new SupportTicket { Id = Guid.NewGuid(), UserId = null, Message = "newer", CreatedAt = now });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var (total, items) = await _repo.GetPagedTicketsAsync(null, null, 1, 10);

        Assert.Equal(2, total);
        Assert.Equal(new[] { "newer", "older" }, items.Select(t => t.Message));
        Assert.Null(items[0].User);
        Assert.Equal(user.Id, items[1].User!.Id);
    }

    [Fact]
    public async Task GetPagedTicketsAsync_FiltersByTypeAndStatus_AndPages()
    {
        var now = DateTime.UtcNow;
        for (var i = 0; i < 3; i++)
            _context.SupportTickets.Add(new SupportTicket { Id = Guid.NewGuid(), Type = SupportTicketType.TechnicalProblem, Message = $"open-{i}", CreatedAt = now.AddMinutes(-i) });
        _context.SupportTickets.Add(new SupportTicket { Id = Guid.NewGuid(), Type = SupportTicketType.TechnicalProblem, Message = "resolved", Status = "Resolved", StatusCode = SupportTicketStatus.Resolved });
        _context.SupportTickets.Add(new SupportTicket { Id = Guid.NewGuid(), Type = SupportTicketType.GeneralQuestion, Message = "question" });
        await _context.SaveChangesAsync();

        var (total, page2) = await _repo.GetPagedTicketsAsync(SupportTicketType.TechnicalProblem, SupportTicketStatus.Open, 2, 2);

        Assert.Equal(3, total);
        Assert.Equal("open-2", Assert.Single(page2).Message);
    }

    [Fact]
    public async Task GetPagedReportsAsync_FiltersByCategoryAndStatus_OrdersNewestFirst_AndPagesInSql()
    {
        var reporter = await SeedUserAsync();
        var reported = await SeedUserAsync();
        var now = DateTime.UtcNow;

        _context.Reports.AddRange(
            new Report { Id = Guid.NewGuid(), ReporterId = reporter.Id, ReportedUserId = reported.Id, Category = ReportCategory.Harassment, Status = "Open", Description = "h-oldest", CreatedAt = now.AddMinutes(-30) },
            new Report { Id = Guid.NewGuid(), ReporterId = reporter.Id, ReportedUserId = reported.Id, Category = ReportCategory.Harassment, Status = "Open", Description = "h-middle", CreatedAt = now.AddMinutes(-20) },
            new Report { Id = Guid.NewGuid(), ReporterId = reporter.Id, ReportedUserId = reported.Id, Category = ReportCategory.Harassment, Status = "Open", Description = "h-newest", CreatedAt = now.AddMinutes(-10) },
            new Report { Id = Guid.NewGuid(), ReporterId = reporter.Id, ReportedUserId = reported.Id, Category = ReportCategory.Harassment, Status = "Resolved", Description = "h-resolved", CreatedAt = now.AddMinutes(-5) },
            new Report { Id = Guid.NewGuid(), ReporterId = reporter.Id, ReportedUserId = reported.Id, Category = ReportCategory.Spam, Status = "Open", Description = "spam-open", CreatedAt = now });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var (total, page1) = await _repo.GetPagedReportsAsync(ReportCategory.Harassment, "Open", 1, 2);
        var (total2, page2) = await _repo.GetPagedReportsAsync(ReportCategory.Harassment, "Open", 2, 2);
        var (total3, page3) = await _repo.GetPagedReportsAsync(ReportCategory.Harassment, "Open", 3, 2);

        Assert.Equal(3, total);
        Assert.Equal(new[] { "h-newest", "h-middle" }, page1.Select(r => r.Description));
        Assert.Equal(reporter.Id, page1[0].Reporter!.Id);
        Assert.Equal(reported.Id, page1[0].ReportedUser!.Id);

        Assert.Equal(3, total2);
        Assert.Equal("h-oldest", Assert.Single(page2).Description);

        Assert.Equal(3, total3);
        Assert.Empty(page3);
    }

    [Fact]
    public async Task GetReportDetailsByIdAsync_AndGetTicketDetailsByIdAsync_LoadNavigations()
    {
        var reporter = await SeedUserAsync();
        var reported = await SeedUserAsync();
        var report = new Report
        {
            Id = Guid.NewGuid(),
            ReporterId = reporter.Id,
            ReportedUserId = reported.Id,
            Category = ReportCategory.Spam,
            Description = "Details test",
            Status = "Open"
        };
        var ticket = new SupportTicket
        {
            Id = Guid.NewGuid(),
            UserId = reporter.Id,
            Type = SupportTicketType.TechnicalProblem,
            Message = "Ticket details",
            ContactEmail = "contact@test.local"
        };
        _context.Reports.Add(report);
        _context.SupportTickets.Add(ticket);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var loadedReport = await _repo.GetReportDetailsByIdAsync(report.Id);
        var loadedTicket = await _repo.GetTicketDetailsByIdAsync(ticket.Id);

        Assert.NotNull(loadedReport);
        Assert.Equal(reporter.Id, loadedReport!.Reporter!.Id);
        Assert.Equal(reported.Id, loadedReport.ReportedUser!.Id);

        Assert.NotNull(loadedTicket);
        Assert.Equal(reporter.Id, loadedTicket!.User!.Id);
    }
}
