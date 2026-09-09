using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Cocorra.BLL.Services.NotificationService;
using Cocorra.DAL.Data;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.NotificationRepository;
using Cocorra.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cocorra.Tests;

public class NotificationServiceTests : IDisposable
{
    private readonly SqliteTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private (NotificationService service, AppDbContext db) CreateService()
    {
        var scope = _host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repo = new NotificationRepository(db);
        var service = new NotificationService(repo);
        return (service, db);
    }

    [Fact]
    public async Task GetMyNotificationsAsync_ReturnsUserNotificationsInDescendingOrder()
    {
        var (service, db) = CreateService();
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();

        db.Users.AddRange(
            new ApplicationUser { Id = userId, UserName = "u1", FirstName = "F1", LastName = "L1" },
            new ApplicationUser { Id = otherUserId, UserName = "u2", FirstName = "F2", LastName = "L2" }
        );

        db.Notifications.AddRange(
            new Notification
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Title = "Old Notice",
                Message = "Old Msg",
                Type = NotificationType.System,
                CreatedAt = DateTime.UtcNow.AddMinutes(-10),
                IsRead = false
            },
            new Notification
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Title = "New Notice",
                Message = "New Msg",
                Type = NotificationType.System,
                CreatedAt = DateTime.UtcNow,
                IsRead = false
            },
            new Notification
            {
                Id = Guid.NewGuid(),
                UserId = otherUserId,
                Title = "Other User Notice",
                Message = "Other Msg",
                Type = NotificationType.System,
                CreatedAt = DateTime.UtcNow,
                IsRead = false
            }
        );
        await db.SaveChangesAsync();

        var result = await service.GetMyNotificationsAsync(userId, pageNumber: 1, pageSize: 10);

        Assert.True(result.Succeeded);
        var items = result.Data!.ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal("New Notice", items[0].Title);
        Assert.Equal("Old Notice", items[1].Title);
    }

    [Fact]
    public async Task MarkNotificationAsReadAsync_NotificationNotFound_ReturnsNotFound()
    {
        var (service, _) = CreateService();

        var result = await service.MarkNotificationAsReadAsync(Guid.NewGuid(), Guid.NewGuid());

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        Assert.Equal("Notification not found.", result.Message);
    }

    [Fact]
    public async Task MarkNotificationAsReadAsync_BelongsToDifferentUser_ReturnsNotFound()
    {
        var (service, db) = CreateService();
        var notificationId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();

        db.Users.AddRange(
            new ApplicationUser { Id = ownerId, UserName = "owner", FirstName = "O", LastName = "W" },
            new ApplicationUser { Id = otherUserId, UserName = "other", FirstName = "O", LastName = "T" }
        );

        db.Notifications.Add(new Notification
        {
            Id = notificationId,
            UserId = ownerId,
            Title = "Title",
            Message = "Msg",
            Type = NotificationType.System,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var result = await service.MarkNotificationAsReadAsync(notificationId, otherUserId);

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
    }

    [Fact]
    public async Task MarkNotificationAsReadAsync_ValidNotification_MarksAsRead()
    {
        var (service, db) = CreateService();
        var notificationId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        db.Users.Add(new ApplicationUser { Id = userId, UserName = "user", FirstName = "U", LastName = "S" });

        db.Notifications.Add(new Notification
        {
            Id = notificationId,
            UserId = userId,
            Title = "Title",
            Message = "Msg",
            Type = NotificationType.System,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var result = await service.MarkNotificationAsReadAsync(notificationId, userId);

        Assert.True(result.Succeeded);
        Assert.Equal("Notification marked as read.", result.Data);

        db.ChangeTracker.Clear();
        var notification = await db.Notifications.FindAsync(notificationId);
        Assert.NotNull(notification);
        Assert.True(notification.IsRead);
    }

    [Fact]
    public async Task MarkAllAsReadAsync_MarksAllUnreadNotificationsForUser()
    {
        var (service, db) = CreateService();
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();

        db.Users.AddRange(
            new ApplicationUser { Id = userId, UserName = "u1", FirstName = "F1", LastName = "L1" },
            new ApplicationUser { Id = otherUserId, UserName = "u2", FirstName = "F2", LastName = "L2" }
        );

        var notif1 = new Notification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = "N1",
            Message = "M1",
            Type = NotificationType.System,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        };
        var notif2 = new Notification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = "N2",
            Message = "M2",
            Type = NotificationType.System,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        };
        var otherNotif = new Notification
        {
            Id = Guid.NewGuid(),
            UserId = otherUserId,
            Title = "Other",
            Message = "Other",
            Type = NotificationType.System,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        };

        db.Notifications.AddRange(notif1, notif2, otherNotif);
        await db.SaveChangesAsync();

        var result = await service.MarkAllAsReadAsync(userId);

        Assert.True(result.Succeeded);
        Assert.Equal("All notifications marked as read.", result.Data);

        db.ChangeTracker.Clear();
        var userNotifs = db.Notifications.Where(n => n.UserId == userId).ToList();
        Assert.All(userNotifs, n => Assert.True(n.IsRead));

        var other = await db.Notifications.FindAsync(otherNotif.Id);
        Assert.NotNull(other);
        Assert.False(other.IsRead);
    }
}
