using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GpSignageWebapp.Migrations
{
    /// <inheritdoc />
    public partial class AddDisplayOrientation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Orientation",
                table: "displays",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Landscape");

            migrationBuilder.UpdateData(
                table: "displays",
                keyColumn: "Id",
                keyValue: new Guid("11111111-1111-1111-1111-111111111111"),
                column: "Orientation",
                value: "Portrait");

            migrationBuilder.UpdateData(
                table: "displays",
                keyColumn: "Id",
                keyValue: new Guid("22222222-2222-2222-2222-222222222222"),
                column: "Orientation",
                value: "Portrait");

            migrationBuilder.UpdateData(
                table: "displays",
                keyColumn: "Id",
                keyValue: new Guid("33333333-3333-3333-3333-333333333333"),
                column: "Orientation",
                value: "Portrait");

            migrationBuilder.UpdateData(
                table: "displays",
                keyColumn: "Id",
                keyValue: new Guid("44444444-4444-4444-4444-444444444444"),
                column: "Orientation",
                value: "Portrait");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Orientation",
                table: "displays");
        }
    }
}
