using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddFeedbackReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FeedbackReport",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    Page = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    AppVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    PublicResponse = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeedbackReport", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FeedbackReport_Client_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Client",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FeedbackReport_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackReport_CompanyId",
                table: "FeedbackReport",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackReport_CreatedAt_Id",
                table: "FeedbackReport",
                columns: new[] { "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackReport_Status_CreatedAt_Id",
                table: "FeedbackReport",
                columns: new[] { "Status", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_FeedbackReport_UserId_CompanyId_CreatedAt_Id",
                table: "FeedbackReport",
                columns: new[] { "UserId", "CompanyId", "CreatedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FeedbackReport");
        }
    }
}
