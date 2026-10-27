using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Commercial.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "whatsapp_consent",
                schema: "members",
                table: "members",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "whatsapp_consent_at",
                schema: "members",
                table: "members",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "whatsapp_consent_by",
                schema: "members",
                table: "members",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "notifications_enabled",
                schema: "members",
                table: "commercial_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "staff_alert_phones",
                schema: "members",
                table: "commercial_settings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "notifications",
                schema: "members",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    recipient_kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    recipient = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    message_text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    provider = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    provider_message_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    membership_end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                    table.ForeignKey(
                        name: "fk_notifications_members_tenant_id_member_id",
                        columns: x => new { x.tenant_id, x.member_id },
                        principalSchema: "members",
                        principalTable: "members",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_notifications_memberships_tenant_id_membership_id",
                        columns: x => new { x.tenant_id, x.membership_id },
                        principalSchema: "members",
                        principalTable: "memberships",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_tenant_id_member_id",
                schema: "members",
                table: "notifications",
                columns: new[] { "tenant_id", "member_id" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_tenant_id_membership_id_type_recipient",
                schema: "members",
                table: "notifications",
                columns: new[] { "tenant_id", "membership_id", "type", "recipient" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notifications_tenant_id_status_created_at",
                schema: "members",
                table: "notifications",
                columns: new[] { "tenant_id", "status", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notifications",
                schema: "members");

            migrationBuilder.DropColumn(
                name: "whatsapp_consent",
                schema: "members",
                table: "members");

            migrationBuilder.DropColumn(
                name: "whatsapp_consent_at",
                schema: "members",
                table: "members");

            migrationBuilder.DropColumn(
                name: "whatsapp_consent_by",
                schema: "members",
                table: "members");

            migrationBuilder.DropColumn(
                name: "notifications_enabled",
                schema: "members",
                table: "commercial_settings");

            migrationBuilder.DropColumn(
                name: "staff_alert_phones",
                schema: "members",
                table: "commercial_settings");
        }
    }
}
