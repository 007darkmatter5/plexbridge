using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace PlexBridge.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<PlexServerConnection> PlexServers => Set<PlexServerConnection>();
    public DbSet<LibrarySnapshot> LibrarySnapshots => Set<LibrarySnapshot>();
    public DbSet<PlexAccountLink> PlexAccountLinks => Set<PlexAccountLink>();
    public DbSet<MigrationAudit> MigrationAudits => Set<MigrationAudit>();
    public DbSet<MigrationAuditItem> MigrationAuditItems => Set<MigrationAuditItem>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<PlexServerConnection>(entity =>
        {
            entity.ToTable("PlexServers");
            entity.Property(x => x.Name).HasMaxLength(80).IsRequired();
            entity.Property(x => x.BaseUrl).HasMaxLength(300).IsRequired();
            entity.Property(x => x.MachineIdentifier).HasMaxLength(200);
            entity.Property(x => x.ServerVersion).HasMaxLength(100);
            entity.Property(x => x.FriendlyName).HasMaxLength(200);
            entity.Property(x => x.Status).HasMaxLength(40).IsRequired();
            entity.Property(x => x.LastError).HasMaxLength(300);
            entity.HasMany(x => x.Libraries)
                .WithOne(x => x.Server)
                .HasForeignKey(x => x.PlexServerConnectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<LibrarySnapshot>(entity =>
        {
            entity.ToTable("LibrarySnapshots");
            entity.Property(x => x.SectionKey).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Title).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Type).HasMaxLength(40).IsRequired();
            entity.HasIndex(x => new { x.PlexServerConnectionId, x.SectionKey }).IsUnique();
        });

        builder.Entity<MigrationAudit>(entity =>
        {
            entity.ToTable("MigrationAudits");
            entity.Property(x => x.UserId).HasMaxLength(450).IsRequired();
            entity.Property(x => x.Categories).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Outcome).HasMaxLength(40).IsRequired();
            entity.Property(x => x.PlexUserUuid).HasMaxLength(80);
            entity.Property(x => x.PlexUserName).HasMaxLength(200);
            entity.HasMany(x => x.Items)
                .WithOne(x => x.Audit)
                .HasForeignKey(x => x.MigrationAuditId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<MigrationAuditItem>(entity =>
        {
            entity.ToTable("MigrationAuditItems");
            entity.Property(x => x.Action).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Outcome).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Title).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Detail).HasMaxLength(300);
        });

        builder.Entity<PlexAccountLink>(entity =>
        {
            entity.ToTable("PlexAccountLinks");
            entity.Property(x => x.UserId).HasMaxLength(450).IsRequired();
            entity.Property(x => x.ClientIdentifier).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Username).HasMaxLength(200);
            entity.Property(x => x.PlexUserId).HasMaxLength(80);
            entity.HasIndex(x => x.UserId).IsUnique();
        });
    }
}
