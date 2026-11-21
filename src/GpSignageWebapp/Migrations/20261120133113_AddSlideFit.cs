using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GpSignageWebapp.Migrations
{
    /// <inheritdoc />
    public partial class AddSlideFit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Fit",
                table: "slides",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Contain");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Fit",
                table: "slides");
        }
    }
}
