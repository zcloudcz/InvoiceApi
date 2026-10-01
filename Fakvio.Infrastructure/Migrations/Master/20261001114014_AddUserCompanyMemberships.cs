using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Fakvio.Infrastructure.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddUserCompanyMemberships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CompanyId",
                table: "OAuthGrant",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CompanyId",
                table: "OAuthAuthorizationCode",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long[]>(
                name: "AllowedCompanyIds",
                table: "ApiKey",
                type: "bigint[]",
                nullable: false,
                defaultValueSql: "ARRAY[]::bigint[]");

            migrationBuilder.AddColumn<long>(
                name: "CompanyId",
                table: "ApiKey",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CompanyMembershipInvitation",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyMembershipInvitation", x => x.Id);
                    table.CheckConstraint("CK_CompanyMembershipInvitation_Role", "\"Role\" IN (0, 1)");
                    table.ForeignKey(
                        name: "FK_CompanyMembershipInvitation_Client_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Client",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CompanyMembershipInvitation_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserCompanyMembership",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    CompanyId = table.Column<long>(type: "bigint", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreationOperationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedByUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserCompanyMembership", x => x.Id);
                    table.CheckConstraint("CK_UserCompanyMembership_Role", "\"Role\" IN (0, 1)");
                    table.ForeignKey(
                        name: "FK_UserCompanyMembership_Client_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Client",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserCompanyMembership_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyMembershipInvitation_CompanyId",
                table: "CompanyMembershipInvitation",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyMembershipInvitation_TokenHash",
                table: "CompanyMembershipInvitation",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompanyMembershipInvitation_UserId",
                table: "CompanyMembershipInvitation",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserCompanyMembership_CompanyId_IsActive",
                table: "UserCompanyMembership",
                columns: new[] { "CompanyId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_UserCompanyMembership_UserId_CompanyId",
                table: "UserCompanyMembership",
                columns: new[] { "UserId", "CompanyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserCompanyMembership_UserId_CreationOperationId",
                table: "UserCompanyMembership",
                columns: new[] { "UserId", "CreationOperationId" },
                unique: true);

            // Preserve each existing default-company assignment as an explicit membership.
            // SysAdmin remains a platform role; it must never become a company role.
            migrationBuilder.Sql("""
                INSERT INTO "UserCompanyMembership" ("UserId", "CompanyId", "Role", "IsActive", "CreatedAt")
                SELECT u."Id", u."CompanyId", u."Role", TRUE, CURRENT_TIMESTAMP
                FROM "User" u JOIN "Client" c ON c."Id" = u."CompanyId"
                WHERE u."Role" IN (0, 1) AND c."IsIssuer";

                UPDATE "OAuthGrant" g SET "CompanyId" = u."CompanyId"
                FROM "User" u WHERE u."Id" = g."UserId" AND u."Role" IN (0, 1);
                UPDATE "OAuthAuthorizationCode" c SET "CompanyId" = u."CompanyId"
                FROM "User" u WHERE u."Id" = c."UserId" AND u."Role" IN (0, 1);

                UPDATE "ApiKey" k SET "CompanyId" = u."CompanyId"
                FROM "User" u WHERE u."Id" = k."UserId" AND u."Role" IN (0, 1) AND k."OAuthGrantId" IS NULL;
                UPDATE "ApiKey" k SET "CompanyId" = g."CompanyId"
                FROM "OAuthGrant" g WHERE g."Id" = k."OAuthGrantId";
                UPDATE "ApiKey" SET "AllowedCompanyIds" = ARRAY["CompanyId"]
                WHERE "CompanyId" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Old binaries derive company access from User.CompanyId. Before removing
            // the explicit bindings, revoke credentials whose meaning would change.
            // A rollback must not silently give a company-B credential access to A.
            migrationBuilder.Sql("""
                UPDATE "ApiKey" k SET "RevokedAt" = CURRENT_TIMESTAMP
                FROM "User" u WHERE u."Id" = k."UserId" AND k."RevokedAt" IS NULL
                    AND u."Role" IN (0, 1)
                    AND (k."CompanyId" IS DISTINCT FROM u."CompanyId" OR cardinality(k."AllowedCompanyIds") > 1
                        OR NOT COALESCE(u."CompanyId" = ANY(k."AllowedCompanyIds"), FALSE)
                        OR NOT EXISTS (SELECT 1 FROM "UserCompanyMembership" m WHERE m."UserId" = u."Id"
                            AND m."CompanyId" = u."CompanyId" AND m."IsActive" AND m."Role" = u."Role"));
                UPDATE "OAuthGrant" g SET "RevokedAt" = CURRENT_TIMESTAMP
                FROM "User" u WHERE u."Id" = g."UserId" AND g."RevokedAt" IS NULL
                    AND (g."CompanyId" IS DISTINCT FROM u."CompanyId"
                        OR NOT EXISTS (SELECT 1 FROM "UserCompanyMembership" m WHERE m."UserId" = u."Id"
                            AND m."CompanyId" = u."CompanyId" AND m."IsActive" AND m."Role" = u."Role"));
                DELETE FROM "OAuthAuthorizationCode" c USING "User" u
                WHERE u."Id" = c."UserId" AND (c."CompanyId" IS DISTINCT FROM u."CompanyId"
                    OR NOT EXISTS (SELECT 1 FROM "UserCompanyMembership" m WHERE m."UserId" = u."Id"
                        AND m."CompanyId" = u."CompanyId" AND m."IsActive" AND m."Role" = u."Role"));
                """);
            migrationBuilder.DropTable(
                name: "CompanyMembershipInvitation");

            migrationBuilder.DropTable(
                name: "UserCompanyMembership");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "OAuthGrant");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "OAuthAuthorizationCode");

            migrationBuilder.DropColumn(
                name: "AllowedCompanyIds",
                table: "ApiKey");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "ApiKey");
        }
    }
}
