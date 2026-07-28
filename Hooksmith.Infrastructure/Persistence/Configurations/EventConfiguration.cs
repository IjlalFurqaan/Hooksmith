using Hooksmith.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hooksmith.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="Event"/> to the <c>events</c> table.</summary>
public sealed class EventConfiguration : IEntityTypeConfiguration<Event>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Event> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("events");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.Type).HasColumnName("type").HasMaxLength(200).IsRequired();

        // jsonb rather than json: it stores parsed, so the dashboard can filter on payload
        // contents later without reparsing every row, and it rejects malformed JSON at write.
        builder.Property(e => e.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();

        builder.Property(e => e.ReceivedAt).HasColumnName("received_at").IsRequired();
        builder.Property(e => e.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(255);

        // The idempotency contract is enforced by the database, not by the read-then-write in
        // the ingest service. Two concurrent requests carrying the same key both find nothing
        // on the read; only one of them can win this index, and the loser re-reads the winner's
        // event instead of fanning out a duplicate.
        builder.HasIndex(e => e.IdempotencyKey)
            .HasDatabaseName("ux_events_idempotency_key")
            .IsUnique()
            .HasFilter("idempotency_key IS NOT NULL");

        builder.HasIndex(e => new { e.Type, e.ReceivedAt }).HasDatabaseName("ix_events_type_received_at");
    }
}
