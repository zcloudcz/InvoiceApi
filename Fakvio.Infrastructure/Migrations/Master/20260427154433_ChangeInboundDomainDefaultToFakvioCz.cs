using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Master
{
    /// <inheritdoc />
    public partial class ChangeInboundDomainDefaultToFakvioCz : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "InboundDomain",
                table: "PaymentMatchingSystemSettings",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "fakvio.cz",
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255,
                oldDefaultValue: "pay.fakvio.cz");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "InboundDomain",
                table: "PaymentMatchingSystemSettings",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "pay.fakvio.cz",
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255,
                oldDefaultValue: "fakvio.cz");
        }
    }
}
