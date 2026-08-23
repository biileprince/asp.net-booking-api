using booking.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace booking.Data;

public sealed class BookingDbContext(DbContextOptions<BookingDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<AvailabilitySlot> AvailabilitySlots => Set<AvailabilitySlot>();
    public DbSet<Booking> Bookings => Set<Booking>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>()
            .HasIndex(user => user.Email)
            .IsUnique();

        modelBuilder.Entity<AvailabilitySlot>()
            .HasIndex(slot => new { slot.ProviderId, slot.StartUtc, slot.EndUtc });

        modelBuilder.Entity<Booking>()
            .HasOne(booking => booking.Slot)
            .WithMany()
            .HasForeignKey(booking => booking.SlotId);
    }
}
