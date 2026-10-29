using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Subscriptions.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "pending_plan_id",
                schema: "subscriptions",
                table: "subscriptions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "pending_plan_version_id",
                schema: "subscriptions",
                table: "subscriptions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "scheduled_cancel_reason",
                schema: "subscriptions",
                table: "subscriptions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "subscription_plan_changes",
                schema: "subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_plan_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_plan_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    effective_on = table.Column<DateOnly>(type: "date", nullable: false),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: true),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscription_plan_changes", x => x.id);
                    table.ForeignKey(
                        name: "fk_subscription_plan_changes_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalSchema: "subscriptions",
                        principalTable: "subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_subscription_plan_changes_subscription_id_requested_at",
                schema: "subscriptions",
                table: "subscription_plan_changes",
                columns: new[] { "subscription_id", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_subscription_plan_changes_tenant_id_status",
                schema: "subscriptions",
                table: "subscription_plan_changes",
                columns: new[] { "tenant_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "subscription_plan_changes",
                schema: "subscriptions");

            migrationBuilder.DropColumn(
                name: "pending_plan_id",
                schema: "subscriptions",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "pending_plan_version_id",
                schema: "subscriptions",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "scheduled_cancel_reason",
                schema: "subscriptions",
                table: "subscriptions");
        }
    }
}
