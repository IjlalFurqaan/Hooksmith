using Hooksmith.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hooksmith.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="Delivery"/> to the <c>deliveries</c> table.</summary>
public sealed class DeliveryConfiguration : IEntityTypeConfiguration<Delivery>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Delivery> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("deliveries");
        builder.HasKey(d => d.Id);

        builder.Property(d => d.Id).HasColumnName("id");
        builder.Property(d => d.EventId).HasColumnName("event_id").IsRequired();
        builder.Property(d => d.SubscriptionId).HasColumnName("subscription_id").IsRequired();

        // Stored as text rather than as the enum's integer. This is an operations tool: being
        // able to type `where status = 'Retrying'` in psql at 3am is worth more than the few
        // bytes per row, and Postgres indexes the text just as happily.
        builder.Property(d => d.Status)
            .HasColumnName("status")
            .HasMaxLength(20)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(d => d.AttemptCount).HasColumnName("attempt_count").IsRequired();
        builder.Property(d => d.NextAttemptAt).HasColumnName("next_attempt_at");
        builder.Property(d => d.CreatedAt).HasColumnName("created_at").IsRequired();

        // The delivery worker's hot query, run on every poll by every instance:
        //   WHERE status IN ('Pending','Retrying') AND next_attempt_at <= now()
        //   ORDER BY next_attempt_at
        //   FOR UPDATE SKIP LOCKED
        // Without this index that is a sequential scan of the entire delivery history, which
        // grows without bound, every few seconds, forever.
        builder.HasIndex(d => new { d.Status, d.NextAttemptAt })
            .HasDatabaseName("ix_deliveries_status_next_attempt_at");

        builder.HasIndex(d => d.SubscriptionId).HasDatabaseName("ix_deliveries_subscription_id");
        builder.HasIndex(d => d.EventId).HasDatabaseName("ix_deliveries_event_id");
        builder.HasIndex(d => d.CreatedAt).HasDatabaseName("ix_deliveries_created_at");

        builder.HasOne<Event>()
            .WithMany()
            .HasForeignKey(d => d.EventId)
            .OnDelete(DeleteBehavior.Cascade);

        // Deleting a subscription takes its delivery history with it. Use PATCH IsActive=false
        // to stop delivering while keeping the audit trail; DELETE means "remove this and
        // everything about it".
        builder.HasOne<Subscription>()
            .WithMany()
            .HasForeignKey(d => d.SubscriptionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
