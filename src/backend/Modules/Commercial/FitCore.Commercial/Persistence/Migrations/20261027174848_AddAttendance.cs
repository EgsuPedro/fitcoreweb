using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Commercial.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAttendance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "access");

            migrationBuilder.AddColumn<string>(
                name: "qr_token_hash",
                schema: "members",
                table: "members",
                type: "character(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "qr_token_issued_at",
                schema: "members",
                table: "members",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "attendance",
                schema: "access",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: true),
                    check_in_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    check_out_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    method = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    check_out_method = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    is_exception = table.Column<bool>(type: "boolean", nullable: false),
                    exception_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    registered_by = table.Column<Guid>(type: "uuid", nullable: false),
                    check_out_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attendance", x => x.id);
                    table.ForeignKey(
                        name: "fk_attendance_members_tenant_id_member_id",
                        columns: x => new { x.tenant_id, x.member_id },
                        principalSchema: "members",
                        principalTable: "members",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_attendance_memberships_tenant_id_membership_id",
                        columns: x => new { x.tenant_id, x.membership_id },
                        principalSchema: "members",
                        principalTable: "memberships",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_members_tenant_id_qr_token_hash",
                schema: "members",
                table: "members",
                columns: new[] { "tenant_id", "qr_token_hash" },
                unique: true,
                filter: "qr_token_hash IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_attendance_tenant_id_branch_id_check_in_at",
                schema: "access",
                table: "attendance",
                columns: new[] { "tenant_id", "branch_id", "check_in_at" });

            migrationBuilder.CreateIndex(
                name: "ix_attendance_tenant_id_member_id_check_in_at",
                schema: "access",
                table: "attendance",
                columns: new[] { "tenant_id", "member_id", "check_in_at" });

            migrationBuilder.CreateIndex(
                name: "ix_attendance_tenant_id_membership_id",
                schema: "access",
                table: "attendance",
                columns: new[] { "tenant_id", "membership_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attendance",
                schema: "access");

            migrationBuilder.DropIndex(
                name: "ix_members_tenant_id_qr_token_hash",
                schema: "members",
                table: "members");

            migrationBuilder.DropColumn(
                name: "qr_token_hash",
                schema: "members",
                table: "members");

            migrationBuilder.DropColumn(
                name: "qr_token_issued_at",
                schema: "members",
                table: "members");
        }
    }
}
