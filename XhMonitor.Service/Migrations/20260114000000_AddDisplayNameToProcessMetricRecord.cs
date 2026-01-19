using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XhMonitor.Service.Data;

#nullable disable

namespace XhMonitor.Service.Migrations
{
    [DbContext(typeof(MonitorDbContext))]
    [Migration("20260114000000_AddDisplayNameToProcessMetricRecord")]
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
