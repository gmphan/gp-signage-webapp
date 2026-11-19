using GpSignageWebapp.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GpSignageWebapp.Data.Configurations;

public class DisplayConfiguration : IEntityTypeConfiguration<Display>
{
    public void Configure(EntityTypeBuilder<Display> builder)
    {
        builder.ToTable("displays");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(d => d.Slug)
            .IsRequired()
            .HasMaxLength(100);

        // Store orientation as a string ("Landscape"/"Portrait") for readability in the DB.
        builder.Property(d => d.Orientation)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(20)
            .HasDefaultValue(DisplayOrientation.Landscape);

        builder.HasIndex(d => d.Slug)
            .IsUnique();

        builder.Property(d => d.CreatedAtUtc)
            .IsRequired();

        builder.Property(d => d.UpdatedAtUtc)
            .IsRequired();

        // Deleting a slide should clear the display's current slide, not cascade-delete the display.
        builder.HasOne(d => d.CurrentSlide)
            .WithMany()
            .HasForeignKey(d => d.CurrentSlideId)
            .OnDelete(DeleteBehavior.SetNull);

        // Seed the default TVs. Slugs are the stable URL keys the Pis load
        // (/player/?display=<slug>) and the SignalR group names.
        // Guid values are FIXED so re-running migrations is deterministic.
        // Menus/ads are shown vertically, so seeded TVs default to Portrait.
        var seededAt = new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc);
        builder.HasData(
            new Display
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Name = "Reception",
                Slug = "reception",
                Orientation = DisplayOrientation.Portrait,
                CreatedAtUtc = seededAt,
                UpdatedAtUtc = seededAt,
            },
            new Display
            {
                Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Name = "Window",
                Slug = "window",
                Orientation = DisplayOrientation.Portrait,
                CreatedAtUtc = seededAt,
                UpdatedAtUtc = seededAt,
            },
            new Display
            {
                Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                Name = "Pedicure",
                Slug = "pedicure",
                Orientation = DisplayOrientation.Portrait,
                CreatedAtUtc = seededAt,
                UpdatedAtUtc = seededAt,
            },
            new Display
            {
                Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                Name = "Manicure",
                Slug = "manicure",
                Orientation = DisplayOrientation.Portrait,
                CreatedAtUtc = seededAt,
                UpdatedAtUtc = seededAt,
            }
        );
    }
}

