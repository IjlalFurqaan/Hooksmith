using Hooksmith.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Hooksmith.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="Subscription"/> to the <c>subscriptions</c> table.</summary>
public sealed class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Subscription> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("subscriptions");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id).HasColumnName("id");
        builder.Property(s => s.Name).HasColumnName("name").HasMaxLength(200).IsRequired();

        // Uri has no native relational mapping. Absolute URIs round-trip losslessly through
        // their original string, so the conversion is a straight ToString/parse pair.
        builder.Property(s => s.Url)
            .HasColumnName("url")
            .HasMaxLength(2000)
            .IsRequired()
            .HasConversion(
                url => url.ToString(),
                value => new Uri(value, UriKind.Absolute));

        builder.Property(s => s.Secret).HasColumnName("secret").HasMaxLength(200).IsRequired();

        builder.Property(s => s.EventTypes)
            .HasColumnName("event_types")
            .HasColumnType("text[]")
            .IsRequired();

        builder.Property(s => s.IsActive).HasColumnName("is_active").IsRequired();
        builder.Property(s => s.CreatedAt).HasColumnName("created_at").IsRequired();

        // Fan-out asks "which active subscriptions want this event type?" on every ingest.
        // A GIN index over the array is what makes that an index lookup rather than a scan.
        builder.HasIndex(s => s.EventTypes)
            .HasDatabaseName("ix_subscriptions_event_types")
            .HasMethod("gin");

        builder.HasIndex(s => s.IsActive).HasDatabaseName("ix_subscriptions_is_active");
    }
}
