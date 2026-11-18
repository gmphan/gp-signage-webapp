using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace GpSignageWebapp.Migrations
{
    /// <inheritdoc />
    public partial class SeedDisplays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "displays",
                columns: new[] { "Id", "CreatedAtUtc", "CurrentSlideId", "LastSeenUtc", "Name", "Slug", "UpdatedAtUtc" },
                values: new object[,]
                {
                    { new Guid("11111111-1111-1111-1111-111111111111"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Reception", "reception", new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("22222222-2222-2222-2222-222222222222"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Window", "window", new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("33333333-3333-3333-3333-333333333333"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Pedicure", "pedicure", new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("44444444-4444-4444-4444-444444444444"), new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc), null, null, "Manicure", "manicure", new DateTime(2026, 11, 18, 0, 0, 0, 0, DateTimeKind.Utc) }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "displays",
                keyColumn: "Id",
                keyValue: new Guid("11111111-1111-1111-1111-111111111111"));

            migrationBuilder.DeleteData(
                table: "displays",
                keyColumn: "Id",
                keyValue: new Guid("22222222-2222-2222-2222-222222222222"));

            migrationBuilder.DeleteData(
                table: "displays",
                keyColumn: "Id",
                keyValue: new Guid("33333333-3333-3333-3333-333333333333"));

            migrationBuilder.DeleteData(
                table: "displays",
                keyColumn: "Id",
                keyValue: new Guid("44444444-4444-4444-4444-444444444444"));
        }
    }
}
