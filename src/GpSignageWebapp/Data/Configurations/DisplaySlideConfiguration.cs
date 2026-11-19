using GpSignageWebapp.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GpSignageWebapp.Data.Configurations;

public class DisplaySlideConfiguration : IEntityTypeConfiguration<DisplaySlide>
{
    public void Configure(EntityTypeBuilder<DisplaySlide> builder)
    {
        builder.ToTable("display_slides");

        // Composite key: a slide appears at most once per display.
        builder.HasKey(ds => new { ds.DisplayId, ds.SlideId });

        builder.Property(ds => ds.SortOrder).IsRequired();
        builder.Property(ds => ds.CreatedAtUtc).IsRequired();

        // Deleting a display removes its playlist entries.
        builder.HasOne(ds => ds.Display)
            .WithMany(d => d.Playlist)
            .HasForeignKey(ds => ds.DisplayId)
            .OnDelete(DeleteBehavior.Cascade);

        // Deleting a slide removes it from every playlist.
        builder.HasOne(ds => ds.Slide)
            .WithMany()
            .HasForeignKey(ds => ds.SlideId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(ds => new { ds.DisplayId, ds.SortOrder });
    }
}
