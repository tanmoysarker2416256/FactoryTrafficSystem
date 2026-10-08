using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Reflection.Emit;

namespace TrafficControl.Infrastructure;

public sealed class TrafficDbContext(DbContextOptions<TrafficDbContext> options) : DbContext(options)
{
    public DbSet<JunctionEntity> Junctions => Set<JunctionEntity>();
    public DbSet<AuditLogEntity> AuditLog => Set<AuditLogEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<JunctionEntity>(e =>
        {
            e.ToTable("Junctions");
            e.HasKey(x => x.Id);
            e.Property(x => x.ConfigJson).IsRequired();
            e.Property(x => x.StateJson).IsRequired();
        });

        modelBuilder.Entity<AuditLogEntity>(e =>
        {
            e.ToTable("AuditLog");
            e.HasKey(x => x.Id);
            e.HasOne<JunctionEntity>().WithMany().HasForeignKey(x => x.JunctionId);
            e.HasIndex(x => new { x.JunctionId, x.Id });
        });
    }
}
