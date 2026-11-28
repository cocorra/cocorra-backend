using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Cocorra.DAL.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cocorra.DAL.Data
{
    /// <summary>Mapping for text rooms, the word dictionary and report message context.</summary>
    internal static class TextRoomModelConfiguration
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private static readonly ValueComparer<List<TextRoomTopic>> TopicListComparer = new(
            (a, b) => JsonSerializer.Serialize(a, JsonOptions) == JsonSerializer.Serialize(b, JsonOptions),
            v => JsonSerializer.Serialize(v, JsonOptions).GetHashCode(),
            v => JsonSerializer.Deserialize<List<TextRoomTopic>>(JsonSerializer.Serialize(v, JsonOptions), JsonOptions) ?? new List<TextRoomTopic>());

        public static void Apply(ModelBuilder builder)
        {
            builder.Entity<TextRoom>(e =>
            {
                e.Property(x => x.Title).HasMaxLength(120).IsRequired();
                e.Property(x => x.Subject).HasMaxLength(60);
                e.Property(x => x.Description).HasMaxLength(1000);
                e.Property(x => x.CloseReason).HasMaxLength(200);
                ConfigureTopicPool(e.Property(x => x.TopicPool));
                e.OwnsOne(x => x.Settings, ConfigureSettings);
                e.Navigation(x => x.Settings).IsRequired();
                e.HasIndex(x => new { x.Status, x.OpensAt });
                e.HasIndex(x => x.ParentRoomId);
                e.HasIndex(x => x.ScheduleId);
            });

            builder.Entity<TextRoomSchedule>(e =>
            {
                e.Property(x => x.Name).HasMaxLength(120).IsRequired();
                ConfigureTopicPool(e.Property(x => x.TopicPool));
                e.OwnsOne(x => x.Settings, ConfigureSettings);
                e.Navigation(x => x.Settings).IsRequired();
                e.HasIndex(x => new { x.IsEnabled, x.NextSpawnAt });
            });

            builder.Entity<TextRoomGlobalSettings>(e =>
            {
                e.Property(x => x.Id).ValueGeneratedNever();
                e.OwnsOne(x => x.Defaults, ConfigureSettings);
                e.Navigation(x => x.Defaults).IsRequired();
            });

            builder.Entity<TextRoomParticipant>(e =>
            {
                e.HasKey(x => new { x.TextRoomId, x.UserId });
                e.HasOne(x => x.TextRoom).WithMany().HasForeignKey(x => x.TextRoomId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(x => x.UserId);
            });

            builder.Entity<TextRoomMessage>(e =>
            {
                e.Property(x => x.Content).HasMaxLength(4000).IsRequired();
                e.HasOne(x => x.TextRoom).WithMany().HasForeignKey(x => x.TextRoomId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne(x => x.Sender).WithMany().HasForeignKey(x => x.SenderId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(x => new { x.TextRoomId, x.Id });
                e.HasIndex(x => new { x.IsReported, x.CreatedAt });
                e.HasIndex(x => x.SenderId);
            });

            builder.Entity<WordDictionaryEntry>(e =>
            {
                e.Property(x => x.Term).HasMaxLength(100).IsRequired();
                e.Property(x => x.NormalizedTerm).HasMaxLength(100).IsRequired();
                e.Property(x => x.Notes).HasMaxLength(200);
                e.HasIndex(x => new { x.NormalizedTerm, x.MatchType }).IsUnique();
                e.HasIndex(x => x.IsActive);
            });

            builder.Entity<ReportMessageContextEntry>(e =>
            {
                e.Property(x => x.SenderDisplayName).HasMaxLength(128).IsRequired();
                e.Property(x => x.Content).HasMaxLength(4000).IsRequired();
                e.HasOne(x => x.Report).WithMany(r => r.MessageContext).HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(x => new { x.ReportId, x.TextRoomMessageId }).IsUnique();
            });

            builder.Entity<Report>(e =>
            {
                e.HasOne(r => r.ReportedTextRoom).WithMany().HasForeignKey(r => r.ReportedTextRoomId).OnDelete(DeleteBehavior.NoAction);
                e.HasIndex(r => r.ReportedTextRoomMessageId);
                e.HasIndex(r => new { r.Severity, r.CreatedAt });
                // Polled every few seconds for pending captures; the second column lets the seek skip
                // every legacy report, which also has a null ContextCompletedAt.
                e.HasIndex(r => new { r.ContextCompletedAt, r.ReportedTextRoomMessageId });
                // Declared so EF keeps it: the filtered index below leads with ReporterId but
                // excludes every row without a text-room message, so it cannot serve the FK.
                e.HasIndex(r => r.ReporterId);
                // One report per user per message; existing rows are all NULL here, so the filter keeps them out.
                e.HasIndex(r => new { r.ReporterId, r.ReportedTextRoomMessageId })
                    .IsUnique()
                    .HasFilter("[ReportedTextRoomMessageId] IS NOT NULL");
            });
        }

        private static void ConfigureTopicPool(PropertyBuilder<List<TextRoomTopic>> property)
        {
            property
                .HasConversion(
                    v => JsonSerializer.Serialize(v, JsonOptions),
                    v => string.IsNullOrEmpty(v)
                        ? new List<TextRoomTopic>()
                        : JsonSerializer.Deserialize<List<TextRoomTopic>>(v, JsonOptions) ?? new List<TextRoomTopic>())
                .Metadata.SetValueComparer(TopicListComparer);
        }

        private static void ConfigureSettings<TOwner>(OwnedNavigationBuilder<TOwner, TextRoomSettings> s) where TOwner : class
        {
            s.Property(x => x.AllowPrivateChatFromRoom).HasDefaultValue(true);
            s.Property(x => x.MaxMessageLength).HasDefaultValue(500);
        }
    }
}
