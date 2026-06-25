using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddMailboxType_v47 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MailboxType",
                table: "MasterMailboxIndex",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<long>(
                name: "TenantInvoiceMailboxId",
                table: "MasterMailboxIndex",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MailboxType",
                table: "MasterMailboxIndex");

            migrationBuilder.DropColumn(
                name: "TenantInvoiceMailboxId",
                table: "MasterMailboxIndex");
        }
    }
}
