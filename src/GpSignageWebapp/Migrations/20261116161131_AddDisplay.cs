using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GpSignageWebapp.Migrations
{
    /// <inheritdoc />
    public partial class AddDisplay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "displays",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CurrentSlideId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_displays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_displays_slides_CurrentSlideId",
                        column: x => x.CurrentSlideId,
                        principalTable: "slides",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_displays_CurrentSlideId",
                table: "displays",
                column: "CurrentSlideId");

            migrationBuilder.CreateIndex(
                name: "IX_displays_Slug",
                table: "displays",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "displays");
        }
    }
}
