using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddClientColor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Color",
                table: "Client",
                type: "character varying(9)",
                maxLength: 9,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Color",
                table: "Client");
        }
    }
}
