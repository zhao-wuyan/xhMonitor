using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace XhMonitor.Service.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AggregatedMetricRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProcessId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProcessName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    AggregationLevel = table.Column<int>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    MetricsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AggregatedMetricRecords", x => x.Id);
                    table.CheckConstraint("CK_AggregatedMetricRecords_MetricsJson_Valid", "json_valid(MetricsJson)");
                });

            migrationBuilder.CreateTable(
                name: "AlertConfigurations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MetricId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Threshold = table.Column<double>(type: "REAL", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertConfigurations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProcessMetricRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProcessId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProcessName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    CommandLine = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    MetricsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessMetricRecords", x => x.Id);
                    table.CheckConstraint("CK_ProcessMetricRecords_MetricsJson_Valid", "json_valid(MetricsJson)");
                });

            migrationBuilder.InsertData(
                table: "AlertConfigurations",
                columns: new[] { "Id", "CreatedAt", "IsEnabled", "MetricId", "Threshold", "UpdatedAt" },
                values: new object[,]
                {
                    { 1, new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, "cpu", 90.0, new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 2, new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, "memory", 90.0, new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 3, new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, "gpu", 90.0, new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { 4, new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), true, "vram", 90.0, new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

            migrationBuilder.CreateIndex(
                name: "IX_AggregatedMetricRecords_AggregationLevel_Timestamp",
                table: "AggregatedMetricRecords",
                columns: new[] { "AggregationLevel", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_AggregatedMetricRecords_ProcessId_AggregationLevel_Timestamp",
                table: "AggregatedMetricRecords",
                columns: new[] { "ProcessId", "AggregationLevel", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_AggregatedMetricRecords_ProcessId_Timestamp",
                table: "AggregatedMetricRecords",
                columns: new[] { "ProcessId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_ProcessMetricRecords_ProcessId_Timestamp",
                table: "ProcessMetricRecords",
                columns: new[] { "ProcessId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_ProcessMetricRecords_Timestamp",
                table: "ProcessMetricRecords",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AggregatedMetricRecords");

            migrationBuilder.DropTable(
                name: "AlertConfigurations");

            migrationBuilder.DropTable(
                name: "ProcessMetricRecords");
        }
    }
}
