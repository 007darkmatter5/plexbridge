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
    public DbSet<WatchdogSetting> WatchdogSettings => Set<WatchdogSetting>();
    public DbSet<WatchdogIncident> WatchdogIncidents => Set<WatchdogIncident>();
    public DbSet<LyricsSetting> LyricsSettings => Set<LyricsSetting>();
    public DbSet<LibraryMount> LibraryMounts => Set<LibraryMount>();

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

        builder.Entity<WatchdogSetting>(entity =>
        {
            entity.ToTable("WatchdogSettings");
            entity.Property(x => x.RecoveryMode).HasMaxLength(20).IsRequired();
            entity.Property(x => x.LogFolder).HasMaxLength(300);
            entity.HasIndex(x => x.PlexServerConnectionId).IsUnique();
            entity.HasOne(x => x.Server)
                .WithMany()
                .HasForeignKey(x => x.PlexServerConnectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<LyricsSetting>(entity =>
        {
            entity.ToTable("LyricsSettings");
            entity.Property(x => x.PathMapsJson).IsRequired();
            entity.Property(x => x.MusicSectionKey).HasMaxLength(16);
            entity.Property(x => x.MusicSectionTitle).HasMaxLength(200);
            entity.Property(x => x.ContainerFolder).HasMaxLength(400);
            entity.HasIndex(x => x.PlexServerConnectionId).IsUnique();
            entity.HasOne(x => x.Server)
                .WithMany()
                .HasForeignKey(x => x.PlexServerConnectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<LibraryMount>(entity =>
        {
            entity.ToTable("LibraryMounts");
            entity.Property(x => x.ContainerFolder).HasMaxLength(400);
            entity.Property(x => x.PathMapsJson).IsRequired();
            entity.HasIndex(x => x.PlexServerConnectionId).IsUnique();
            entity.HasOne(x => x.Server)
                .WithMany()
                .HasForeignKey(x => x.PlexServerConnectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<WatchdogIncident>(entity =>
        {
            entity.ToTable("WatchdogIncidents");
            entity.Property(x => x.ServerName).HasMaxLength(80).IsRequired();
            entity.Property(x => x.PlaySessionId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.SessionKey).HasMaxLength(128);
            entity.Property(x => x.TranscodeSessionId).HasMaxLength(128);
            entity.Property(x => x.DisplayTitle).HasMaxLength(300).IsRequired();
            entity.Property(x => x.AccountTitle).HasMaxLength(200);
            entity.Property(x => x.PlayerProduct).HasMaxLength(80);
            entity.Property(x => x.PlayerPlatform).HasMaxLength(80);
            entity.Property(x => x.PlayerDevice).HasMaxLength(80);
            entity.Property(x => x.Guids).HasMaxLength(500);
            entity.Property(x => x.RatingKey).HasMaxLength(128);
            entity.Property(x => x.FilePath).HasMaxLength(500);
            entity.Property(x => x.PartId).HasMaxLength(128);
            entity.Property(x => x.Container).HasMaxLength(40);
            entity.Property(x => x.VideoCodec).HasMaxLength(40);
            entity.Property(x => x.AudioCodec).HasMaxLength(40);
            entity.Property(x => x.VideoResolution).HasMaxLength(40);
            entity.Property(x => x.PlayDecision).HasMaxLength(40).IsRequired();
            entity.Property(x => x.SubtitleCodec).HasMaxLength(40);
            entity.Property(x => x.StallRule).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Resolution).HasMaxLength(40).IsRequired();
            entity.Property(x => x.TimelineJson).IsRequired();
            entity.Property(x => x.ProposedAction).HasMaxLength(300);
            entity.Property(x => x.ActionKind).HasMaxLength(40);
            entity.Property(x => x.ActionStatus).HasMaxLength(40).IsRequired();
            entity.Property(x => x.ActionDetail).HasMaxLength(300);
            entity.HasIndex(x => new { x.PlexServerConnectionId, x.StartedUtc });
            entity.HasOne(x => x.Server)
                .WithMany()
                .HasForeignKey(x => x.PlexServerConnectionId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
