using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Commercial.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMemberUserLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "user_id",
                schema: "members",
                table: "members",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_members_tenant_id_user_id",
                schema: "members",
                table: "members",
                columns: new[] { "tenant_id", "user_id" },
                unique: true,
                filter: "user_id IS NOT NULL");

            // La cuenta del alumno es un usuario del mismo gimnasio: la base rechaza vínculos con usuarios que no existen.
            migrationBuilder.Sql("""
                ALTER TABLE members.members ADD CONSTRAINT fk_members_user
                  FOREIGN KEY (user_id) REFERENCES identity.app_users (id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE members.members DROP CONSTRAINT IF EXISTS fk_members_user;");

            migrationBuilder.DropIndex(
                name: "ix_members_tenant_id_user_id",
                schema: "members",
                table: "members");

            migrationBuilder.DropColumn(
                name: "user_id",
                schema: "members",
                table: "members");
        }
    }
}
