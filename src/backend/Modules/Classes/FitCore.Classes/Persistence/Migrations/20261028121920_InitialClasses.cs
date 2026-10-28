using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Classes.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialClasses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "classes");

            migrationBuilder.CreateTable(
                name: "group_classes",
                schema: "classes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    activity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    instructor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    capacity = table.Column<int>(type: "integer", nullable: false),
                    level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    requirements = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    min_booking_notice_minutes = table.Column<int>(type: "integer", nullable: false),
                    max_booking_advance_days = table.Column<int>(type: "integer", nullable: false),
                    cancellation_deadline_minutes = table.Column<int>(type: "integer", nullable: false),
                    waitlist_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    waitlist_capacity = table.Column<int>(type: "integer", nullable: false),
                    offer_window_minutes = table.Column<int>(type: "integer", nullable: false),
                    require_active_membership = table.Column<bool>(type: "boolean", nullable: false),
                    apply_payment_policy = table.Column<bool>(type: "boolean", nullable: false),
                    min_age = table.Column<int>(type: "integer", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_group_classes", x => x.id);
                    table.UniqueConstraint("ak_group_classes_tenant_id_id", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_group_classes_tenant_id_activity",
                schema: "classes",
                table: "group_classes",
                columns: new[] { "tenant_id", "activity" });

            migrationBuilder.CreateIndex(
                name: "ix_group_classes_tenant_id_branch_id_is_active",
                schema: "classes",
                table: "group_classes",
                columns: new[] { "tenant_id", "branch_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_group_classes_tenant_id_instructor_id",
                schema: "classes",
                table: "group_classes",
                columns: new[] { "tenant_id", "instructor_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "group_classes",
                schema: "classes");
        }
    }
}
