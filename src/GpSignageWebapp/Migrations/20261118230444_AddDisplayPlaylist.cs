using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GpSignageWebapp.Migrations
{
    /// <inheritdoc />
    public partial class AddDisplayPlaylist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "display_slides",
                columns: table => new
                {
                    DisplayId = table.Column<Guid>(type: "uuid", nullable: false),
                    SlideId = table.Column<Guid>(type: "uuid", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_display_slides", x => new { x.DisplayId, x.SlideId });
                    table.ForeignKey(
                        name: "FK_display_slides_displays_DisplayId",
                        column: x => x.DisplayId,
                        principalTable: "displays",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_display_slides_slides_SlideId",
                        column: x => x.SlideId,
                        principalTable: "slides",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_display_slides_DisplayId_SortOrder",
                table: "display_slides",
                columns: new[] { "DisplayId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_display_slides_SlideId",
                table: "display_slides",
                column: "SlideId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "display_slides");
        }
    }
}
