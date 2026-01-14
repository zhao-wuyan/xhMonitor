using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XhMonitor.Service.Migrations
{
    /// <inheritdoc />
    public partial class AddDisplayNameToProcessMetricRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "ProcessMetricRecords",
                type: "TEXT",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "ProcessMetricRecords");
        }
    }
}
