using Cocorra.DAL.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Cocorra.DAL.Data
{
    public class AppDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        public DbSet<Room> Rooms { get; set; }
        public DbSet<RoomParticipant> RoomParticipants { get; set; }
        public DbSet<RoomTopicRequest> RoomTopicRequests { get; set; }
        public DbSet<TopicVote> TopicVotes { get; set; }
        public DbSet<Notification> Notifications { get; set; }
        public DbSet<FriendRequest> FriendRequests { get; set; }
        public DbSet<RoomReminder> RoomReminders { get; set; }
        public DbSet<Message> Messages { get; set; }
        public DbSet<SupportTicket> SupportTickets { get; set; }
        public DbSet<Report> Reports { get; set; }
        public DbSet<UserBlock> UserBlocks { get; set; }
        public DbSet<SupportChat> SupportChats { get; set; }
        public DbSet<SupportMessage> SupportMessages { get; set; }
        public DbSet<BlockedDevices> BlockedDevices { get; set; }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            // ============================================================
            // 1. إعدادات جدول المشاركين (RoomParticipant)
            // ============================================================

            // ⚠️ ده السطر اللي كان ناقص وحل المشكلة
            // بنقوله إن المفتاح هو (RoomId + UserId) مع بعض
            builder.Entity<RoomParticipant>()
                .HasKey(p => new { p.RoomId, p.UserId });

            // العلاقات (Cascade & Restrict)
            builder.Entity<RoomParticipant>()
                .HasOne(p => p.Room)
                .WithMany(r => r.Participants)
                .HasForeignKey(p => p.RoomId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<RoomParticipant>()
                .HasOne(p => p.User)
                .WithMany(u => u.RoomParticipations)
                .HasForeignKey(p => p.UserId)
                .OnDelete(DeleteBehavior.Restrict);


            // ============================================================
            // 2. إعدادات جدول التصويت (TopicVote)
            // ============================================================

            // ⚠️ نفس الكلام هنا، مفتاح مركب
            builder.Entity<TopicVote>()
                .HasKey(v => new { v.UserId, v.TopicRequestId });

            builder.Entity<TopicVote>()
                .HasOne(v => v.TopicRequest)
                .WithMany()
                .HasForeignKey(v => v.TopicRequestId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<TopicVote>()
                .HasOne(v => v.User)
                .WithMany()
                .HasForeignKey(v => v.UserId)
                .OnDelete(DeleteBehavior.Restrict);


            // ============================================================
            // 3. إعدادات جدول الغرفة (Room)
            // ============================================================
            builder.Entity<Room>()
                .HasOne(r => r.Host)
                .WithMany(u => u.OwnedRooms)
                .HasForeignKey(r => r.HostId)
                .OnDelete(DeleteBehavior.Restrict);


            // ============================================================
            // 4. إعدادات جدول طلبات المواضيع (RoomTopicRequest)
            // ============================================================
            builder.Entity<RoomTopicRequest>()
                .HasOne(r => r.Requester)
                .WithMany()
                .HasForeignKey(r => r.RequesterId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.Entity<RoomTopicRequest>()
                .HasOne(r => r.TargetCoach)
                .WithMany()
                .HasForeignKey(r => r.TargetCoachId)
                .OnDelete(DeleteBehavior.Restrict);


            builder.Entity<RoomReminder>()
        .HasKey(rr => new { rr.UserId, rr.RoomId });

            builder.Entity<FriendRequest>()
                .HasOne(fr => fr.Sender)
                .WithMany()
                .HasForeignKey(fr => fr.SenderId)
                .OnDelete(DeleteBehavior.Restrict); 

            builder.Entity<FriendRequest>()
                .HasOne(fr => fr.Receiver)
                .WithMany()
                .HasForeignKey(fr => fr.ReceiverId)
                .OnDelete(DeleteBehavior.Restrict);


            builder.Entity<Message>()
        .HasOne(m => m.Sender)
        .WithMany()
        .HasForeignKey(m => m.SenderId)
        .OnDelete(DeleteBehavior.Restrict);

            builder.Entity<Message>()
                .HasOne(m => m.Receiver)
                .WithMany()
                .HasForeignKey(m => m.ReceiverId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.Entity<Message>()
    .HasIndex(m => new { m.SenderId, m.ReceiverId, m.CreatedAt });
            builder.Entity<Message>()
    .HasIndex(m => new { m.ReceiverId, m.IsRead });
            builder.Entity<FriendRequest>()
                .HasIndex(fr => new { fr.SenderId, fr.ReceiverId })
                .IsUnique();

            builder.Entity<Notification>()
                .HasIndex(n => new { n.UserId, n.CreatedAt });

            builder.Entity<Room>()
                .HasIndex(r => r.Status)
                .HasDatabaseName("IX_Rooms_Status");

            // ============================================================
            // 5. Reports (Prevent cascade cycle on Reporter)
            // ============================================================
            builder.Entity<Report>()
                .HasOne(r => r.Reporter)
                .WithMany()
                .HasForeignKey(r => r.ReporterId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.Entity<Report>()
                .HasOne(r => r.ReportedUser)
                .WithMany()
                .HasForeignKey(r => r.ReportedUserId)
                .OnDelete(DeleteBehavior.SetNull);

            builder.Entity<SupportTicket>()
                .HasOne(st => st.User)
                .WithMany()
                .HasForeignKey(st => st.UserId)
                .OnDelete(DeleteBehavior.SetNull);

            // ============================================================
            // 6. User Blocks
            // ============================================================
            builder.Entity<UserBlock>()
                .HasOne(ub => ub.Blocker)
                .WithMany()
                .HasForeignKey(ub => ub.BlockerId)
                .OnDelete(DeleteBehavior.NoAction);

            builder.Entity<UserBlock>()
                .HasOne(ub => ub.Blocked)
                .WithMany()
                .HasForeignKey(ub => ub.BlockedId)
                .OnDelete(DeleteBehavior.NoAction);

            builder.Entity<UserBlock>()
                .HasOne(ub => ub.BlockedDevice)
                .WithMany(bd => bd.UserBlocks)
                .HasForeignKey(ub => ub.BlockedDeviceId)
                .OnDelete(DeleteBehavior.NoAction);

            // ============================================================
            // 8. Blocked Devices
            // ============================================================
            builder.Entity<BlockedDevices>()
                .HasOne(bd => bd.ApplicationUser)
                .WithMany(u => u.BlockedDevices)
                .HasForeignKey(bd => bd.ApplicationUserId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Entity<BlockedDevices>()
                .HasIndex(bd => bd.DeviceId);

            // ============================================================
            // 7. Support Chat Indexes
            // ============================================================

            // Covers: GetUserOpenChatAsync, GetUserChatHistoryAsync
            builder.Entity<SupportChat>()
                .HasIndex(c => new { c.UserId, c.Status })
                .HasDatabaseName("IX_SupportChats_UserId_Status");

            // Covers: GetPendingChatsAsync (+ sorts by CreatedAt)
            builder.Entity<SupportChat>()
                .HasIndex(c => new { c.Status, c.CreatedAt })
                .HasDatabaseName("IX_SupportChats_Status_CreatedAt");

            // Covers: GetAdminActiveChatsAsync
            builder.Entity<SupportChat>()
                .HasIndex(c => new { c.AdminId, c.Status })
                .HasDatabaseName("IX_SupportChats_AdminId_Status");

            // Covers: GetPendingUserMessageCountAsync
            builder.Entity<SupportMessage>()
                .HasIndex(m => new { m.SupportChatId, m.IsFromAdmin })
                .HasDatabaseName("IX_SupportMessages_ChatId_IsFromAdmin");
        }   

    }
}