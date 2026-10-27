using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Commercial.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlansPromotionsTaxes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "billing");

            migrationBuilder.CreateTable(
                name: "commercial_settings",
                schema: "members",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    access_policy = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    min_payment_percent = table.Column<int>(type: "integer", nullable: false),
                    default_due_days = table.Column<int>(type: "integer", nullable: false),
                    max_discount_percent = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_commercial_settings", x => x.tenant_id);
                });

            migrationBuilder.CreateTable(
                name: "membership_plans",
                schema: "members",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    duration_value = table.Column<int>(type: "integer", nullable: false),
                    duration_unit = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    allowed_access = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    restrictions = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    commercial_terms = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    tax_kind = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: true),
                    valid_until = table.Column<DateOnly>(type: "date", nullable: true),
                    allow_freeze = table.Column<bool>(type: "boolean", nullable: false),
                    max_freeze_days = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_membership_plans", x => x.id);
                    table.UniqueConstraint("ak_membership_plans_tenant_id_id", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "promotions",
                schema: "members",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    discount_type = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    value = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_until = table.Column<DateOnly>(type: "date", nullable: false),
                    max_uses = table.Column<int>(type: "integer", nullable: true),
                    used_count = table.Column<int>(type: "integer", nullable: false),
                    restrictions = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_promotions", x => x.id);
                    table.UniqueConstraint("ak_promotions_tenant_id_id", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "tax_rates",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_until = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tax_rates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "plan_branches",
                schema: "members",
                columns: table => new
                {
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plan_branches", x => new { x.plan_id, x.branch_id });
                    table.ForeignKey(
                        name: "fk_plan_branches_membership_plans_tenant_id_plan_id",
                        columns: x => new { x.tenant_id, x.plan_id },
                        principalSchema: "members",
                        principalTable: "membership_plans",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "promotion_plans",
                schema: "members",
                columns: table => new
                {
                    promotion_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_promotion_plans", x => new { x.promotion_id, x.plan_id });
                    table.ForeignKey(
                        name: "fk_promotion_plans_membership_plans_tenant_id_plan_id",
                        columns: x => new { x.tenant_id, x.plan_id },
                        principalSchema: "members",
                        principalTable: "membership_plans",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_promotion_plans_promotions_tenant_id_promotion_id",
                        columns: x => new { x.tenant_id, x.promotion_id },
                        principalSchema: "members",
                        principalTable: "promotions",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_membership_plans_tenant_id_is_active",
                schema: "members",
                table: "membership_plans",
                columns: new[] { "tenant_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_plan_branches_tenant_id",
                schema: "members",
                table: "plan_branches",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_plan_branches_tenant_id_plan_id",
                schema: "members",
                table: "plan_branches",
                columns: new[] { "tenant_id", "plan_id" });

            migrationBuilder.CreateIndex(
                name: "ix_promotion_plans_tenant_id",
                schema: "members",
                table: "promotion_plans",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_promotion_plans_tenant_id_plan_id",
                schema: "members",
                table: "promotion_plans",
                columns: new[] { "tenant_id", "plan_id" });

            migrationBuilder.CreateIndex(
                name: "ix_promotion_plans_tenant_id_promotion_id",
                schema: "members",
                table: "promotion_plans",
                columns: new[] { "tenant_id", "promotion_id" });

            migrationBuilder.CreateIndex(
                name: "ix_promotions_tenant_id_code",
                schema: "members",
                table: "promotions",
                columns: new[] { "tenant_id", "code" },
                unique: true,
                filter: "code IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tax_rates_kind_valid_from",
                schema: "billing",
                table: "tax_rates",
                columns: new[] { "kind", "valid_from" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "commercial_settings",
                schema: "members");

            migrationBuilder.DropTable(
                name: "plan_branches",
                schema: "members");

            migrationBuilder.DropTable(
                name: "promotion_plans",
                schema: "members");

            migrationBuilder.DropTable(
                name: "tax_rates",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "membership_plans",
                schema: "members");

            migrationBuilder.DropTable(
                name: "promotions",
                schema: "members");
        }
    }
}
