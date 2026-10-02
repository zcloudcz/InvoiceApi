using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class AddRecurringShiftPeriods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ShiftBaselineOccurrence",
                table: "RecurringInvoiceSchedule",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "ShiftPeriodsInText",
                table: "RecurringInvoiceSchedule",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShiftBaselineOccurrence",
                table: "RecurringInvoiceSchedule");

            migrationBuilder.DropColumn(
                name: "ShiftPeriodsInText",
                table: "RecurringInvoiceSchedule");
        }
    }
}
