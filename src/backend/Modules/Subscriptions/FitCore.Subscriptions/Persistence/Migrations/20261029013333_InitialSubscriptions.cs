using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Subscriptions.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "subscriptions");

            migrationBuilder.CreateTable(
                name: "plans",
                schema: "subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    rules = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plans", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "plan_versions",
                schema: "subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    period = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    trial_days = table.Column<int>(type: "integer", nullable: false),
                    grace_days = table.Column<int>(type: "integer", nullable: true),
                    change_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plan_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_plan_versions_plans_plan_id",
                        column: x => x.plan_id,
                        principalSchema: "subscriptions",
                        principalTable: "plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "plan_features",
                schema: "subscriptions",
                columns: table => new
                {
                    plan_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plan_features", x => new { x.plan_version_id, x.code });
                    table.ForeignKey(
                        name: "fk_plan_features_plan_versions_plan_version_id",
                        column: x => x.plan_version_id,
                        principalSchema: "subscriptions",
                        principalTable: "plan_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "plan_limits",
                schema: "subscriptions",
                columns: table => new
                {
                    plan_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    value = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plan_limits", x => new { x.plan_version_id, x.code });
                    table.ForeignKey(
                        name: "fk_plan_limits_plan_versions_plan_version_id",
                        column: x => x.plan_version_id,
                        principalSchema: "subscriptions",
                        principalTable: "plan_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_plan_versions_plan_id_number",
                schema: "subscriptions",
                table: "plan_versions",
                columns: new[] { "plan_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_plans_code",
                schema: "subscriptions",
                table: "plans",
                column: "code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "plan_features",
                schema: "subscriptions");

            migrationBuilder.DropTable(
                name: "plan_limits",
                schema: "subscriptions");

            migrationBuilder.DropTable(
                name: "plan_versions",
                schema: "subscriptions");

            migrationBuilder.DropTable(
                name: "plans",
                schema: "subscriptions");
        }
    }
}
