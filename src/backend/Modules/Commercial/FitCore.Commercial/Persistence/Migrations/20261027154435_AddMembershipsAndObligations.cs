using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Commercial.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMembershipsAndObligations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // EF detectó un cambio de nombre de la clave alterna. Soltarla y recrearla falla porque otras tablas dependen de ella: se renombra la restricción.
            migrationBuilder.Sql("ALTER TABLE members.membership_plans RENAME CONSTRAINT ak_membership_plans_tenant_id_id TO ak_plans_tenant_id_id;");

            migrationBuilder.CreateTable(
                name: "memberships",
                schema: "members",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    plan_price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    duration_value = table.Column<int>(type: "integer", nullable: false),
                    duration_unit = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    tax_kind = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    tax_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    allowed_access = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    commercial_terms = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    allow_freeze = table.Column<bool>(type: "boolean", nullable: false),
                    max_freeze_days = table.Column<int>(type: "integer", nullable: false),
                    promotion_id = table.Column<Guid>(type: "uuid", nullable: true),
                    promotion_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    promotion_code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    discount_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    agreed_price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    freeze_started_on = table.Column<DateOnly>(type: "date", nullable: true),
                    frozen_days_used = table.Column<int>(type: "integer", nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_by = table.Column<Guid>(type: "uuid", nullable: true),
                    cancel_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    renewed_from_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_memberships", x => x.id);
                    table.UniqueConstraint("ak_memberships_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_memberships_members_tenant_id_member_id",
                        columns: x => new { x.tenant_id, x.member_id },
                        principalSchema: "members",
                        principalTable: "members",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_memberships_plans_tenant_id_plan_id",
                        columns: x => new { x.tenant_id, x.plan_id },
                        principalSchema: "members",
                        principalTable: "membership_plans",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_memberships_promotions_tenant_id_promotion_id",
                        columns: x => new { x.tenant_id, x.promotion_id },
                        principalSchema: "members",
                        principalTable: "promotions",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "membership_events",
                schema: "members",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_membership_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_membership_events_memberships_tenant_id_membership_id",
                        columns: x => new { x.tenant_id, x.membership_id },
                        principalSchema: "members",
                        principalTable: "memberships",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_obligations",
                schema: "billing",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reference = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    concept = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    issue_date = table.Column<DateOnly>(type: "date", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    original_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    payable_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    subtotal = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    tax_kind = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    tax_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    voided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    voided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    void_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_obligations", x => x.id);
                    table.UniqueConstraint("ak_payment_obligations_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_payment_obligations_members_tenant_id_member_id",
                        columns: x => new { x.tenant_id, x.member_id },
                        principalSchema: "members",
                        principalTable: "members",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payment_obligations_memberships_tenant_id_membership_id",
                        columns: x => new { x.tenant_id, x.membership_id },
                        principalSchema: "members",
                        principalTable: "memberships",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_membership_events_tenant_id_membership_id_occurred_at",
                schema: "members",
                table: "membership_events",
                columns: new[] { "tenant_id", "membership_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_memberships_tenant_id_branch_id_status_end_date",
                schema: "members",
                table: "memberships",
                columns: new[] { "tenant_id", "branch_id", "status", "end_date" });

            migrationBuilder.CreateIndex(
                name: "ix_memberships_tenant_id_member_id_start_date",
                schema: "members",
                table: "memberships",
                columns: new[] { "tenant_id", "member_id", "start_date" });

            migrationBuilder.CreateIndex(
                name: "ix_memberships_tenant_id_plan_id",
                schema: "members",
                table: "memberships",
                columns: new[] { "tenant_id", "plan_id" });

            migrationBuilder.CreateIndex(
                name: "ix_memberships_tenant_id_promotion_id",
                schema: "members",
                table: "memberships",
                columns: new[] { "tenant_id", "promotion_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_obligations_tenant_id_branch_id_status_due_date",
                schema: "billing",
                table: "payment_obligations",
                columns: new[] { "tenant_id", "branch_id", "status", "due_date" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_obligations_tenant_id_member_id_status",
                schema: "billing",
                table: "payment_obligations",
                columns: new[] { "tenant_id", "member_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_obligations_tenant_id_membership_id",
                schema: "billing",
                table: "payment_obligations",
                columns: new[] { "tenant_id", "membership_id" });

            migrationBuilder.CreateIndex(
                name: "ix_payment_obligations_tenant_id_reference",
                schema: "billing",
                table: "payment_obligations",
                columns: new[] { "tenant_id", "reference" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "membership_events",
                schema: "members");

            migrationBuilder.DropTable(
                name: "payment_obligations",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "memberships",
                schema: "members");

            migrationBuilder.Sql("ALTER TABLE members.membership_plans RENAME CONSTRAINT ak_plans_tenant_id_id TO ak_membership_plans_tenant_id_id;");
        }
    }
}
