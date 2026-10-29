using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Subscriptions.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformPanel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "platform_notes",
                schema: "subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platform_notes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_usage_snapshots",
                schema: "subscriptions",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branches = table.Column<int>(type: "integer", nullable: false),
                    internal_users = table.Column<int>(type: "integer", nullable: false),
                    members = table.Column<int>(type: "integer", nullable: false),
                    measured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_usage_snapshots", x => x.tenant_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_platform_notes_tenant_id_created_at",
                schema: "subscriptions",
                table: "platform_notes",
                columns: new[] { "tenant_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "platform_notes",
                schema: "subscriptions");

            migrationBuilder.DropTable(
                name: "tenant_usage_snapshots",
                schema: "subscriptions");
        }
    }
}
