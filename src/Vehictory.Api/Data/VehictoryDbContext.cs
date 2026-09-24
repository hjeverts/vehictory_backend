using Vehictory.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Vehictory.Api.Data;

public class VehictoryDbContext(DbContextOptions<VehictoryDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<FuelEntry> FuelEntries => Set<FuelEntry>();
    public DbSet<MaintenanceType> MaintenanceTypes => Set<MaintenanceType>();
    public DbSet<MaintenanceEntry> MaintenanceEntries => Set<MaintenanceEntry>();
    public DbSet<MaintenanceAttachment> MaintenanceAttachments => Set<MaintenanceAttachment>();
    public DbSet<RecurringCost> RecurringCosts => Set<RecurringCost>();
    public DbSet<VehicleShare> VehicleShares => Set<VehicleShare>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Email)
            .IsUnique();

        modelBuilder.Entity<Vehicle>()
            .HasOne(v => v.User)
            .WithMany(u => u.Vehicles)
            .HasForeignKey(v => v.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Vehicle>()
            .OwnsMany(v => v.Afschrijvingstabel, staffel => staffel.ToJson());

        modelBuilder.Entity<FuelEntry>()
            .HasOne(f => f.Vehicle)
            .WithMany(v => v.FuelEntries)
            .HasForeignKey(f => f.VehicleId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MaintenanceEntry>()
            .HasOne(m => m.Vehicle)
            .WithMany(v => v.MaintenanceEntries)
            .HasForeignKey(m => m.VehicleId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MaintenanceEntry>()
            .HasOne(m => m.MaintenanceType)
            .WithMany(t => t.MaintenanceEntries)
            .HasForeignKey(m => m.MaintenanceTypeId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<MaintenanceAttachment>()
            .HasOne(a => a.MaintenanceEntry)
            .WithMany(m => m.Attachments)
            .HasForeignKey(a => a.MaintenanceEntryId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<RecurringCost>(e =>
        {
            e.HasOne(r => r.Vehicle)
                .WithMany(v => v.RecurringCosts)
                .HasForeignKey(r => r.VehicleId)
                .OnDelete(DeleteBehavior.Cascade);
            e.Property(r => r.Soort).HasConversion<string>().HasMaxLength(32);
            e.Property(r => r.Frequentie).HasConversion<string>().HasMaxLength(16);
        });

        modelBuilder.Entity<VehicleShare>()
            .HasOne(s => s.Vehicle)
            .WithMany(v => v.Shares)
            .HasForeignKey(s => s.VehicleId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<VehicleShare>()
            .HasOne(s => s.User)
            .WithMany(u => u.SharedVehicles)
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<VehicleShare>()
            .HasIndex(s => new { s.VehicleId, s.UserId })
            .IsUnique();

        modelBuilder.Entity<RefreshToken>()
            .HasOne(r => r.User)
            .WithMany()
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<RefreshToken>()
            .HasIndex(r => r.TokenHash)
            .IsUnique();
    }
}
