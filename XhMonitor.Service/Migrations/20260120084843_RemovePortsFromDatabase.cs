using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace XhMonitor.Service.Migrations
{
    /// <inheritdoc />
    public partial class RemovePortsFromDatabase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "ApplicationSettings",
                keyColumn: "Id",
                keyValue: 9);

            migrationBuilder.DeleteData(
                table: "ApplicationSettings",
                keyColumn: "Id",
                keyValue: 10);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "ApplicationSettings",
                columns: new[] { "Id", "Category", "CreatedAt", "Key", "UpdatedAt", "Value" },
                values: new object[,]
                {
                    { 9, "System", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "SignalRPort", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "35179" },
                    { 10, "System", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "WebPort", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "35180" }
                });
        }
    }
}
