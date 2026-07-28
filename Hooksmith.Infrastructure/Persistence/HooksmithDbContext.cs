using Hooksmith.Core;
using Microsoft.EntityFrameworkCore;

namespace Hooksmith.Infrastructure.Persistence;

/// <summary>
/// The Hooksmith database context.
/// </summary>
/// <remarks>
/// The domain records from <c>Hooksmith.Core</c> are mapped directly rather than copied into
/// a parallel set of persistence models. Core stays free of EF because all the mapping lives
/// here in configuration classes, not in attributes on the domain types.
/// </remarks>
public sealed class HooksmithDbContext : DbContext
{
    /// <summary>Initialises the context.</summary>
    /// <param name="options">Provider and connection configuration.</param>
    public HooksmithDbContext(DbContextOptions<HooksmithDbContext> options)
        : base(options)
    {
    }

    /// <summary>Registered subscriber endpoints.</summary>
    public DbSet<Subscription> Subscriptions => Set<Subscription>();

    /// <summary>Events accepted from producers.</summary>
    public DbSet<Event> Events => Set<Event>();

    /// <summary>One row per event per matching subscription.</summary>
    public DbSet<Delivery> Deliveries => Set<Delivery>();

    /// <summary>The append-only attempt log.</summary>
    public DbSet<Attempt> Attempts => Set<Attempt>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(HooksmithDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
