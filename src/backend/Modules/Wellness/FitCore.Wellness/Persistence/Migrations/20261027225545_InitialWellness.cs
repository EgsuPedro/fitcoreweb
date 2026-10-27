using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Wellness.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialWellness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "training");

            migrationBuilder.CreateTable(
                name: "trainer_profiles",
                schema: "training",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    specialties = table.Column<string[]>(type: "text[]", nullable: false),
                    certifications = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    bio = table.Column<string>(type: "character varying(1500)", maxLength: 1500, nullable: true),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    joined_on = table.Column<DateOnly>(type: "date", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_trainer_profiles", x => x.id);
                    table.UniqueConstraint("ak_trainers_tenant_id_id", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "trainer_assignments",
                schema: "training",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    trainer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_on = table.Column<DateOnly>(type: "date", nullable: false),
                    assigned_by = table.Column<Guid>(type: "uuid", nullable: true),
                    ended_on = table.Column<DateOnly>(type: "date", nullable: true),
                    ended_by = table.Column<Guid>(type: "uuid", nullable: true),
                    end_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_trainer_assignments", x => x.id);
                    table.UniqueConstraint("ak_trainer_assignments_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_trainer_assignments_trainers_tenant_id_trainer_id",
                        columns: x => new { x.tenant_id, x.trainer_id },
                        principalSchema: "training",
                        principalTable: "trainer_profiles",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "trainer_availability",
                schema: "training",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    trainer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<int>(type: "integer", nullable: false),
                    start_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    end_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_trainer_availability", x => x.id);
                    table.ForeignKey(
                        name: "fk_trainer_availability_trainers_tenant_id_trainer_id",
                        columns: x => new { x.tenant_id, x.trainer_id },
                        principalSchema: "training",
                        principalTable: "trainer_profiles",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "trainer_notes",
                schema: "training",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    trainer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_trainer_notes", x => x.id);
                    table.ForeignKey(
                        name: "fk_trainer_notes_trainers_tenant_id_trainer_id",
                        columns: x => new { x.tenant_id, x.trainer_id },
                        principalSchema: "training",
                        principalTable: "trainer_profiles",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_trainer_assignments_tenant_id_member_id_assigned_on",
                schema: "training",
                table: "trainer_assignments",
                columns: new[] { "tenant_id", "member_id", "assigned_on" });

            migrationBuilder.CreateIndex(
                name: "ix_trainer_assignments_tenant_id_trainer_id_ended_on",
                schema: "training",
                table: "trainer_assignments",
                columns: new[] { "tenant_id", "trainer_id", "ended_on" });

            migrationBuilder.CreateIndex(
                name: "ux_trainer_assignments_active_member",
                schema: "training",
                table: "trainer_assignments",
                column: "member_id",
                unique: true,
                filter: "ended_on IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_trainer_availability_tenant_id_trainer_id_day_start_time",
                schema: "training",
                table: "trainer_availability",
                columns: new[] { "tenant_id", "trainer_id", "day", "start_time" });

            migrationBuilder.CreateIndex(
                name: "ix_trainer_notes_tenant_id_member_id_created_at",
                schema: "training",
                table: "trainer_notes",
                columns: new[] { "tenant_id", "member_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_trainer_notes_tenant_id_trainer_id",
                schema: "training",
                table: "trainer_notes",
                columns: new[] { "tenant_id", "trainer_id" });

            migrationBuilder.CreateIndex(
                name: "ix_trainer_profiles_tenant_id_status",
                schema: "training",
                table: "trainer_profiles",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_trainer_profiles_tenant_id_user_id",
                schema: "training",
                table: "trainer_profiles",
                columns: new[] { "tenant_id", "user_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "trainer_assignments",
                schema: "training");

            migrationBuilder.DropTable(
                name: "trainer_availability",
                schema: "training");

            migrationBuilder.DropTable(
                name: "trainer_notes",
                schema: "training");

            migrationBuilder.DropTable(
                name: "trainer_profiles",
                schema: "training");
        }
    }
}
