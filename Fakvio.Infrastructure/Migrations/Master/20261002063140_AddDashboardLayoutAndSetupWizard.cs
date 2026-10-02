using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddDashboardLayoutAndSetupWizard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DashboardLayoutJson",
                table: "UserPreferences",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SetupWizardDismissedAt",
                table: "UserPreferences",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DashboardLayoutJson",
                table: "UserPreferences");

            migrationBuilder.DropColumn(
                name: "SetupWizardDismissedAt",
                table: "UserPreferences");
        }
    }
}
