using Hooksmith.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hooksmith.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="Attempt"/> to the <c>attempts</c> table.</summary>
public sealed class AttemptConfiguration : IEntityTypeConfiguration<Attempt>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Attempt> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("attempts");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Id).HasColumnName("id");
        builder.Property(a => a.DeliveryId).HasColumnName("delivery_id").IsRequired();
        builder.Property(a => a.AttemptNumber).HasColumnName("attempt_number").IsRequired();
        builder.Property(a => a.StatusCode).HasColumnName("status_code");

        // Already capped at 2 KB by Attempt.Create, but the column length is stated anyway so
        // the constraint survives someone inserting outside the domain factory.
        builder.Property(a => a.ResponseBody)
            .HasColumnName("response_body")
            .HasMaxLength(Attempt.MaxResponseBodyBytes);

        builder.Property(a => a.Error).HasColumnName("error").HasMaxLength(2000);
        builder.Property(a => a.DurationMs).HasColumnName("duration_ms").IsRequired();
        builder.Property(a => a.AttemptedAt).HasColumnName("attempted_at").IsRequired();

        // The delivery detail screen reads the whole timeline for one delivery in order.
        builder.HasIndex(a => new { a.DeliveryId, a.AttemptNumber })
            .HasDatabaseName("ix_attempts_delivery_id_attempt_number");

        builder.HasOne<Delivery>()
            .WithMany()
            .HasForeignKey(a => a.DeliveryId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
