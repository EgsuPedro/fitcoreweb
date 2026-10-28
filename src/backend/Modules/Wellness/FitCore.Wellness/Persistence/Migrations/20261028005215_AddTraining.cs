using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Wellness.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTraining : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "plans",
                schema: "training",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    current_version = table.Column<int>(type: "integer", nullable: false),
                    current_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plans", x => x.id);
                    table.UniqueConstraint("ak_plans_tenant_id_id", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "plan_versions",
                schema: "training",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    goal = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    estimated_minutes = table.Column<int>(type: "integer", nullable: true),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: true),
                    valid_until = table.Column<DateOnly>(type: "date", nullable: true),
                    change_note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plan_versions", x => x.id);
                    table.UniqueConstraint("ak_plan_versions_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_plan_versions_plans_tenant_id_plan_id",
                        columns: x => new { x.tenant_id, x.plan_id },
                        principalSchema: "training",
                        principalTable: "plans",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "plan_days",
                schema: "training",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plan_days", x => x.id);
                    table.UniqueConstraint("ak_plan_days_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_plan_days_plan_versions_tenant_id_version_id",
                        columns: x => new { x.tenant_id, x.version_id },
                        principalSchema: "training",
                        principalTable: "plan_versions",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "training_assignments",
                schema: "training",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    ended_on = table.Column<DateOnly>(type: "date", nullable: true),
                    end_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    assigned_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_training_assignments", x => x.id);
                    table.UniqueConstraint("ak_training_assignments_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_training_assignments_plan_versions_tenant_id_plan_version_id",
                        columns: x => new { x.tenant_id, x.plan_version_id },
                        principalSchema: "training",
                        principalTable: "plan_versions",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_training_assignments_plans_tenant_id_plan_id",
                        columns: x => new { x.tenant_id, x.plan_id },
                        principalSchema: "training",
                        principalTable: "plans",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "plan_exercises",
                schema: "training",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day_id = table.Column<Guid>(type: "uuid", nullable: false),
                    exercise_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    exercise_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    tracking_mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    sets = table.Column<int>(type: "integer", nullable: false),
                    reps = table.Column<int>(type: "integer", nullable: true),
                    duration_seconds = table.Column<int>(type: "integer", nullable: true),
                    rest_seconds = table.Column<int>(type: "integer", nullable: false),
                    recommended_load_kg = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: true),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plan_exercises", x => x.id);
                    table.UniqueConstraint("ak_plan_exercises_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_plan_exercises_exercises_exercise_id",
                        column: x => x.exercise_id,
                        principalSchema: "training",
                        principalTable: "exercises",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_plan_exercises_plan_days_tenant_id_day_id",
                        columns: x => new { x.tenant_id, x.day_id },
                        principalSchema: "training",
                        principalTable: "plan_days",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workout_sessions",
                schema: "training",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day_id = table.Column<Guid>(type: "uuid", nullable: false),
                    performed_on = table.Column<DateOnly>(type: "date", nullable: false),
                    duration_minutes = table.Column<int>(type: "integer", nullable: true),
                    member_notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    trainer_comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    trainer_comment_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    recorded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workout_sessions", x => x.id);
                    table.UniqueConstraint("ak_workout_sessions_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_workout_sessions_plan_days_tenant_id_day_id",
                        columns: x => new { x.tenant_id, x.day_id },
                        principalSchema: "training",
                        principalTable: "plan_days",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_workout_sessions_plan_versions_tenant_id_plan_version_id",
                        columns: x => new { x.tenant_id, x.plan_version_id },
                        principalSchema: "training",
                        principalTable: "plan_versions",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_workout_sessions_training_assignments_tenant_id_assignment_",
                        columns: x => new { x.tenant_id, x.assignment_id },
                        principalSchema: "training",
                        principalTable: "training_assignments",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workout_session_exercises",
                schema: "training",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_exercise_id = table.Column<Guid>(type: "uuid", nullable: false),
                    exercise_id = table.Column<Guid>(type: "uuid", nullable: false),
                    exercise_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    tracking_mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    completed = table.Column<bool>(type: "boolean", nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workout_session_exercises", x => x.id);
                    table.UniqueConstraint("ak_workout_session_exercises_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_workout_session_exercises_plan_exercises_tenant_id_plan_exe",
                        columns: x => new { x.tenant_id, x.plan_exercise_id },
                        principalSchema: "training",
                        principalTable: "plan_exercises",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_workout_session_exercises_workout_sessions_tenant_id_sessio",
                        columns: x => new { x.tenant_id, x.session_id },
                        principalSchema: "training",
                        principalTable: "workout_sessions",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workout_sets",
                schema: "training",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_exercise_id = table.Column<Guid>(type: "uuid", nullable: false),
                    set_number = table.Column<int>(type: "integer", nullable: false),
                    reps = table.Column<int>(type: "integer", nullable: true),
                    duration_seconds = table.Column<int>(type: "integer", nullable: true),
                    load_kg = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workout_sets", x => x.id);
                    table.ForeignKey(
                        name: "fk_workout_sets_workout_session_exercises_tenant_id_session_ex",
                        columns: x => new { x.tenant_id, x.session_exercise_id },
                        principalSchema: "training",
                        principalTable: "workout_session_exercises",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_plan_days_tenant_id_version_id",
                schema: "training",
                table: "plan_days",
                columns: new[] { "tenant_id", "version_id" });

            migrationBuilder.CreateIndex(
                name: "ix_plan_days_version_id_position",
                schema: "training",
                table: "plan_days",
                columns: new[] { "version_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_plan_exercises_day_id_position",
                schema: "training",
                table: "plan_exercises",
                columns: new[] { "day_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_plan_exercises_exercise_id",
                schema: "training",
                table: "plan_exercises",
                column: "exercise_id");

            migrationBuilder.CreateIndex(
                name: "ix_plan_exercises_tenant_id_day_id",
                schema: "training",
                table: "plan_exercises",
                columns: new[] { "tenant_id", "day_id" });

            migrationBuilder.CreateIndex(
                name: "ix_plan_versions_plan_id_number",
                schema: "training",
                table: "plan_versions",
                columns: new[] { "plan_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_plan_versions_tenant_id_plan_id",
                schema: "training",
                table: "plan_versions",
                columns: new[] { "tenant_id", "plan_id" });

            migrationBuilder.CreateIndex(
                name: "ix_plans_tenant_id_status",
                schema: "training",
                table: "plans",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_training_assignments_tenant_id_member_id_start_date",
                schema: "training",
                table: "training_assignments",
                columns: new[] { "tenant_id", "member_id", "start_date" });

            migrationBuilder.CreateIndex(
                name: "ix_training_assignments_tenant_id_plan_id",
                schema: "training",
                table: "training_assignments",
                columns: new[] { "tenant_id", "plan_id" });

            migrationBuilder.CreateIndex(
                name: "ix_training_assignments_tenant_id_plan_version_id",
                schema: "training",
                table: "training_assignments",
                columns: new[] { "tenant_id", "plan_version_id" });

            migrationBuilder.CreateIndex(
                name: "ux_training_assignments_open_member",
                schema: "training",
                table: "training_assignments",
                column: "member_id",
                unique: true,
                filter: "ended_on IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_workout_session_exercises_session_id_plan_exercise_id",
                schema: "training",
                table: "workout_session_exercises",
                columns: new[] { "session_id", "plan_exercise_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workout_session_exercises_tenant_id_exercise_id",
                schema: "training",
                table: "workout_session_exercises",
                columns: new[] { "tenant_id", "exercise_id" });

            migrationBuilder.CreateIndex(
                name: "ix_workout_session_exercises_tenant_id_plan_exercise_id",
                schema: "training",
                table: "workout_session_exercises",
                columns: new[] { "tenant_id", "plan_exercise_id" });

            migrationBuilder.CreateIndex(
                name: "ix_workout_session_exercises_tenant_id_session_id",
                schema: "training",
                table: "workout_session_exercises",
                columns: new[] { "tenant_id", "session_id" });

            migrationBuilder.CreateIndex(
                name: "ix_workout_sessions_tenant_id_assignment_id",
                schema: "training",
                table: "workout_sessions",
                columns: new[] { "tenant_id", "assignment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_workout_sessions_tenant_id_day_id",
                schema: "training",
                table: "workout_sessions",
                columns: new[] { "tenant_id", "day_id" });

            migrationBuilder.CreateIndex(
                name: "ix_workout_sessions_tenant_id_member_id_performed_on",
                schema: "training",
                table: "workout_sessions",
                columns: new[] { "tenant_id", "member_id", "performed_on" });

            migrationBuilder.CreateIndex(
                name: "ix_workout_sessions_tenant_id_plan_version_id",
                schema: "training",
                table: "workout_sessions",
                columns: new[] { "tenant_id", "plan_version_id" });

            migrationBuilder.CreateIndex(
                name: "ix_workout_sets_session_exercise_id_set_number",
                schema: "training",
                table: "workout_sets",
                columns: new[] { "session_exercise_id", "set_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workout_sets_tenant_id_session_exercise_id",
                schema: "training",
                table: "workout_sets",
                columns: new[] { "tenant_id", "session_exercise_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workout_sets",
                schema: "training");

            migrationBuilder.DropTable(
                name: "workout_session_exercises",
                schema: "training");

            migrationBuilder.DropTable(
                name: "plan_exercises",
                schema: "training");

            migrationBuilder.DropTable(
                name: "workout_sessions",
                schema: "training");

            migrationBuilder.DropTable(
                name: "plan_days",
                schema: "training");

            migrationBuilder.DropTable(
                name: "training_assignments",
                schema: "training");

            migrationBuilder.DropTable(
                name: "plan_versions",
                schema: "training");

            migrationBuilder.DropTable(
                name: "plans",
                schema: "training");
        }
    }
}
