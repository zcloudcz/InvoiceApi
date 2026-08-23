using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddAiSystemPromptToSystemConfiguration_v146 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AiSystemPromptAppendix",
                table: "SystemConfiguration",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AiSystemPromptCustom",
                table: "SystemConfiguration",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AiSystemPromptAppendix",
                table: "SystemConfiguration");

            migrationBuilder.DropColumn(
                name: "AiSystemPromptCustom",
                table: "SystemConfiguration");
        }
    }
}
