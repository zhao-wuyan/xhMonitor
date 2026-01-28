using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace XhMonitor.Service.Migrations
{
    /// <inheritdoc />
    public partial class AddApplicationSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApplicationSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Category = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationSettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "ApplicationSettings",
                columns: new[] { "Id", "Category", "CreatedAt", "Key", "UpdatedAt", "Value" },
                values: new object[,]
                {
                    { 1, "Appearance", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "ThemeColor", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "\"Dark\"" },
                    { 2, "Appearance", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "Opacity", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "90" },
                    { 3, "DataCollection", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "ProcessKeywords", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "[\"python\",\"llama\"]" },
                    { 4, "DataCollection", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "SystemInterval", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "1000" },
                    { 5, "DataCollection", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "ProcessInterval", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "5000" },
                    { 6, "DataCollection", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "TopProcessCount", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "10" },
                    { 7, "DataCollection", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "DataRetentionDays", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "30" },
                    { 8, "System", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "StartWithWindows", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "false" },
                    { 9, "System", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "SignalRPort", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "35179" },
                    { 10, "System", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "WebPort", new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "35180" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationSettings_Category_Key",
                table: "ApplicationSettings",
                columns: new[] { "Category", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApplicationSettings");
        }
    }
}
