using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace XhMonitor.Service.Migrations
{
    /// <inheritdoc />
    public partial class RemoveIntervalsFromDatabase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "ApplicationSettings",
                keyColumn: "Id",
                keyValue: 4);

            migrationBuilder.DeleteData(
                table: "ApplicationSettings",
                keyColumn: "Id",
                keyValue: 5);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "ApplicationSettings",
                columns: new[] { "Id", "Category", "CreatedAt", "Key", "UpdatedAt", "Value" },
                values: new object[,]
                {
                    { 4, "DataCollection", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "SystemInterval", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "1000" },
                    { 5, "DataCollection", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "ProcessInterval", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "5000" }
                });
        }
    }
}
