using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace XhMonitor.Service.Migrations
{
    /// <inheritdoc />
    public partial class AddMonitoringSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "ApplicationSettings",
                columns: new[] { "Id", "Category", "CreatedAt", "Key", "UpdatedAt", "Value" },
                values: new object[,]
                {
                    { 9, "Monitoring", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "MonitorCpu", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "true" },
                    { 10, "Monitoring", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "MonitorMemory", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "true" },
                    { 11, "Monitoring", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "MonitorGpu", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "true" },
                    { 12, "Monitoring", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "MonitorVram", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "true" },
                    { 13, "Monitoring", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "MonitorPower", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "true" },
                    { 14, "Monitoring", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "MonitorNetwork", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "true" },
                    { 15, "Monitoring", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "AdminMode", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "false" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "ApplicationSettings",
                keyColumn: "Id",
                keyValue: 9);

            migrationBuilder.DeleteData(
                table: "ApplicationSettings",
                keyColumn: "Id",
                keyValue: 10);

            migrationBuilder.DeleteData(
                table: "ApplicationSettings",
                keyColumn: "Id",
                keyValue: 11);

            migrationBuilder.DeleteData(
                table: "ApplicationSettings",
                keyColumn: "Id",
                keyValue: 12);

            migrationBuilder.DeleteData(
                table: "ApplicationSettings",
                keyColumn: "Id",
                keyValue: 13);

            migrationBuilder.DeleteData(
                table: "ApplicationSettings",
                keyColumn: "Id",
                keyValue: 14);

            migrationBuilder.DeleteData(
                table: "ApplicationSettings",
                keyColumn: "Id",
                keyValue: 15);
        }
    }
}
