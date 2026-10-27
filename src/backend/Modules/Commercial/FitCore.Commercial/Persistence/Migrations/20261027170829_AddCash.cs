using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Commercial.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "cash");

            migrationBuilder.AddColumn<Guid>(
                name: "cash_session_id",
                schema: "billing",
                table: "payments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cash_registers",
                schema: "cash",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    allow_multiple_open_sessions = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cash_registers", x => x.id);
                    table.UniqueConstraint("ak_cash_registers_tenant_id_id", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "cash_sessions",
                schema: "cash",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    register_id = table.Column<Guid>(type: "uuid", nullable: false),
                    exclusive = table.Column<bool>(type: "boolean", nullable: false),
                    opened_by = table.Column<Guid>(type: "uuid", nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    opening_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    open_notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    closed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    counted_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    expected_at_close = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    difference = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    close_notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cash_sessions", x => x.id);
                    table.UniqueConstraint("ak_cash_sessions_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_cash_sessions_cash_registers_tenant_id_register_id",
                        columns: x => new { x.tenant_id, x.register_id },
                        principalSchema: "cash",
                        principalTable: "cash_registers",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cash_movements",
                schema: "cash",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    direction = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cash_movements", x => x.id);
                    table.ForeignKey(
                        name: "fk_cash_movements_cash_sessions_tenant_id_session_id",
                        columns: x => new { x.tenant_id, x.session_id },
                        principalSchema: "cash",
                        principalTable: "cash_sessions",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cash_movements_payments_tenant_id_payment_id",
                        columns: x => new { x.tenant_id, x.payment_id },
                        principalSchema: "billing",
                        principalTable: "payments",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_payments_tenant_id_cash_session_id",
                schema: "billing",
                table: "payments",
                columns: new[] { "tenant_id", "cash_session_id" });

            migrationBuilder.CreateIndex(
                name: "ix_cash_movements_tenant_id_payment_id",
                schema: "cash",
                table: "cash_movements",
                columns: new[] { "tenant_id", "payment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_cash_movements_tenant_id_session_id_created_at",
                schema: "cash",
                table: "cash_movements",
                columns: new[] { "tenant_id", "session_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_cash_registers_tenant_id_branch_id_is_active",
                schema: "cash",
                table: "cash_registers",
                columns: new[] { "tenant_id", "branch_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_cash_sessions_tenant_id_branch_id_opened_at",
                schema: "cash",
                table: "cash_sessions",
                columns: new[] { "tenant_id", "branch_id", "opened_at" });

            migrationBuilder.CreateIndex(
                name: "ix_cash_sessions_tenant_id_opened_by_status",
                schema: "cash",
                table: "cash_sessions",
                columns: new[] { "tenant_id", "opened_by", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_cash_sessions_tenant_id_register_id_status",
                schema: "cash",
                table: "cash_sessions",
                columns: new[] { "tenant_id", "register_id", "status" });

            migrationBuilder.AddForeignKey(
                name: "fk_payments_cash_sessions_tenant_id_cash_session_id",
                schema: "billing",
                table: "payments",
                columns: new[] { "tenant_id", "cash_session_id" },
                principalSchema: "cash",
                principalTable: "cash_sessions",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_payments_cash_sessions_tenant_id_cash_session_id",
                schema: "billing",
                table: "payments");

            migrationBuilder.DropTable(
                name: "cash_movements",
                schema: "cash");

            migrationBuilder.DropTable(
                name: "cash_sessions",
                schema: "cash");

            migrationBuilder.DropTable(
                name: "cash_registers",
                schema: "cash");

            migrationBuilder.DropIndex(
                name: "ix_payments_tenant_id_cash_session_id",
                schema: "billing",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "cash_session_id",
                schema: "billing",
                table: "payments");
        }
    }
}
