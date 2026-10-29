using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Subscriptions.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantEntitlements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_entitlements",
                schema: "subscriptions",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    plan_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    plan_version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    access_mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    features = table.Column<string[]>(type: "text[]", nullable: false),
                    limits = table.Column<string>(type: "jsonb", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_entitlements", x => x.tenant_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_entitlements_subscription_id",
                schema: "subscriptions",
                table: "tenant_entitlements",
                column: "subscription_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_entitlements",
                schema: "subscriptions");
        }
    }
}
