using Cocorra.DAL.Models;
using Cocorra.DAL.Models.Analytics;
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
        public DbSet<UserEvent> UserEvents { get; set; }
        public DbSet<DeadLetterEvent> DeadLetterEvents { get; set; }
        public DbSet<DailyStateSnapshot> DailyStateSnapshots { get; set; }
        public DbSet<DailyPlatformMetrics> DailyPlatformMetrics { get; set; }
        public DbSet<DailyRoomMetrics> DailyRoomMetrics { get; set; }
        public DbSet<DailyHostMetrics> DailyHostMetrics { get; set; }
        public DbSet<DailyFunnelMetrics> DailyFunnelMetrics { get; set; }
        public DbSet<AggregationCheckpoint> AggregationCheckpoints { get; set; }

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
            // Analytics Indexes — support fast time-windowed aggregate queries
            // ============================================================

            // User growth: filter/bucket by registration date
            builder.Entity<ApplicationUser>()
                .HasIndex(u => u.CreatedAt)
                .HasDatabaseName("IX_Users_CreatedAt");

            // Report insights: filter by report creation date
            builder.Entity<Report>()
                .HasIndex(r => r.CreatedAt)
                .HasDatabaseName("IX_Reports_CreatedAt");

            // Participation analytics: filter by join date and sort by spoken time
            builder.Entity<RoomParticipant>()
                .HasIndex(p => p.JoinedAt)
                .HasDatabaseName("IX_RoomParticipants_JoinedAt");

            builder.Entity<RoomParticipant>()
                .HasIndex(p => p.TotalSpokenSeconds)
                .HasDatabaseName("IX_RoomParticipants_TotalSpokenSeconds");

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

            // ============================================================
            // 9. User Events Tracking Config
            // ============================================================
            builder.Entity<UserEvent>(e =>
            {
                // Unique idempotency key
                e.HasIndex(x => x.EventId)
                 .IsUnique()
                 .HasDatabaseName("UX_UserEvents_EventId");

                // Filtered index on CorrelationId
                e.HasIndex(x => x.CorrelationId)
                 .HasFilter("[CorrelationId] IS NOT NULL")
                 .HasDatabaseName("IX_UserEvents_CorrelationId");

                // Indexes for filtering
                e.HasIndex(x => new { x.EventType, x.OccurredAtUtc });
                e.HasIndex(x => new { x.UserId, x.OccurredAtUtc });
                // Room-scoped analytics (most-active-room, empty-room rate, …)
                e.HasIndex(x => new { x.RoomId, x.EventType, x.OccurredAtUtc });

                // SetNull on User deletion to keep anonymous event stats
                e.HasOne(x => x.User)
                 .WithMany()
                 .HasForeignKey(x => x.UserId)
                 .OnDelete(DeleteBehavior.SetNull);
            });

            // ============================================================
            // 10. Analytics Read Models & Infrastructure
            // ============================================================
            builder.Entity<DeadLetterEvent>(e =>
            {
                e.HasIndex(x => x.EventId);
                e.HasIndex(x => x.DeadLetteredAtUtc);
            });

            builder.Entity<DailyStateSnapshot>(e =>
            {
                // Unique constraint: one row per (Date, MetricKey) for idempotent snapshot runs
                e.HasIndex(x => new { x.Date, x.MetricKey })
                 .IsUnique()
                 .HasDatabaseName("UX_DailyStateSnapshots_Date_MetricKey");
            });

            builder.Entity<DailyPlatformMetrics>(e =>
            {
                // RM-1: Grain is one row per Date
                e.HasIndex(x => x.Date)
                 .IsUnique()
                 .HasDatabaseName("UX_DailyPlatformMetrics_Date");
            });

            builder.Entity<DailyRoomMetrics>(e =>
            {
                // RM-2: Grain is one row per (Date, RoomId)
                e.HasIndex(x => new { x.Date, x.RoomId })
                 .IsUnique()
                 .HasDatabaseName("UX_DailyRoomMetrics_Date_RoomId");
            });

            builder.Entity<DailyHostMetrics>(e =>
            {
                // RM-3: Grain is one row per (Date, HostId)
                e.HasIndex(x => new { x.Date, x.HostId })
                 .IsUnique()
                 .HasDatabaseName("UX_DailyHostMetrics_Date_HostId");
            });

            builder.Entity<DailyFunnelMetrics>(e =>
            {
                // RM-4: Grain is one row per (CohortDate, FunnelName, StepIndex)
                e.HasIndex(x => new { x.CohortDate, x.FunnelName, x.StepIndex })
                 .IsUnique()
                 .HasDatabaseName("UX_DailyFunnelMetrics_CohortDate_Funnel_Step");
            });

            builder.Entity<AggregationCheckpoint>(e =>
            {
                e.HasIndex(x => x.PipelineName)
                 .IsUnique()
                 .HasDatabaseName("UX_AggregationCheckpoints_PipelineName");
            });
        }

    }
}