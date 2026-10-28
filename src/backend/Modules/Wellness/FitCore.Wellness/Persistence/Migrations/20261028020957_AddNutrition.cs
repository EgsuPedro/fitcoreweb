using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Wellness.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNutrition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "nutrition");

            migrationBuilder.RenameIndex(
                name: "ix_plans_tenant_id_status",
                schema: "training",
                table: "plans",
                newName: "ix_plans_tenant_id_status1");

            migrationBuilder.RenameIndex(
                name: "ix_plan_versions_tenant_id_plan_id",
                schema: "training",
                table: "plan_versions",
                newName: "ix_plan_versions_tenant_id_plan_id1");

            migrationBuilder.RenameIndex(
                name: "ix_plan_versions_plan_id_number",
                schema: "training",
                table: "plan_versions",
                newName: "ix_plan_versions_plan_id_number1");

            migrationBuilder.CreateTable(
                name: "dietary_profiles",
                schema: "nutrition",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    allergies = table.Column<string[]>(type: "text[]", nullable: false),
                    intolerances = table.Column<string[]>(type: "text[]", nullable: false),
                    restrictions = table.Column<string[]>(type: "text[]", nullable: false),
                    notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    recorded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dietary_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "plans",
                schema: "nutrition",
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
                    table.UniqueConstraint("ak_nutrition_plans_tenant_id_id", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "plan_versions",
                schema: "nutrition",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    goal = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    target_calories = table.Column<int>(type: "integer", nullable: true),
                    target_protein_g = table.Column<int>(type: "integer", nullable: true),
                    target_carbs_g = table.Column<int>(type: "integer", nullable: true),
                    target_fat_g = table.Column<int>(type: "integer", nullable: true),
                    recommendations = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: true),
                    valid_until = table.Column<DateOnly>(type: "date", nullable: true),
                    change_note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plan_versions", x => x.id);
                    table.UniqueConstraint("ak_nutrition_versions_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_plan_versions_plans_tenant_id_plan_id",
                        columns: x => new { x.tenant_id, x.plan_id },
                        principalSchema: "nutrition",
                        principalTable: "plans",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "meals",
                schema: "nutrition",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    suggested_time = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_meals", x => x.id);
                    table.UniqueConstraint("ak_nutrition_meals_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_meals_nutrition_versions_tenant_id_version_id",
                        columns: x => new { x.tenant_id, x.version_id },
                        principalSchema: "nutrition",
                        principalTable: "plan_versions",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "nutrition_assignments",
                schema: "nutrition",
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
                    table.PrimaryKey("pk_nutrition_assignments", x => x.id);
                    table.UniqueConstraint("ak_nutrition_assignments_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_nutrition_assignments_nutrition_plans_tenant_id_plan_id",
                        columns: x => new { x.tenant_id, x.plan_id },
                        principalSchema: "nutrition",
                        principalTable: "plans",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_nutrition_assignments_nutrition_versions_tenant_id_plan_ver",
                        columns: x => new { x.tenant_id, x.plan_version_id },
                        principalSchema: "nutrition",
                        principalTable: "plan_versions",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "meal_items",
                schema: "nutrition",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    meal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    food_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(9,2)", precision: 9, scale: 2, nullable: false),
                    unit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    calories = table.Column<int>(type: "integer", nullable: true),
                    protein_g = table.Column<decimal>(type: "numeric(6,1)", precision: 6, scale: 1, nullable: true),
                    carbs_g = table.Column<decimal>(type: "numeric(6,1)", precision: 6, scale: 1, nullable: true),
                    fat_g = table.Column<decimal>(type: "numeric(6,1)", precision: 6, scale: 1, nullable: true),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_meal_items", x => x.id);
                    table.UniqueConstraint("ak_meal_items_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_meal_items_meals_tenant_id_meal_id",
                        columns: x => new { x.tenant_id, x.meal_id },
                        principalSchema: "nutrition",
                        principalTable: "meals",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "meal_logs",
                schema: "nutrition",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    meal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    logged_on = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    recorded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_meal_logs", x => x.id);
                    table.ForeignKey(
                        name: "fk_meal_logs_nutrition_assignments_tenant_id_assignment_id",
                        columns: x => new { x.tenant_id, x.assignment_id },
                        principalSchema: "nutrition",
                        principalTable: "nutrition_assignments",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_meal_logs_nutrition_meals_tenant_id_meal_id",
                        columns: x => new { x.tenant_id, x.meal_id },
                        principalSchema: "nutrition",
                        principalTable: "meals",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_meal_logs_nutrition_versions_tenant_id_plan_version_id",
                        columns: x => new { x.tenant_id, x.plan_version_id },
                        principalSchema: "nutrition",
                        principalTable: "plan_versions",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_dietary_profiles_tenant_id_member_id_recorded_at",
                schema: "nutrition",
                table: "dietary_profiles",
                columns: new[] { "tenant_id", "member_id", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "ix_meal_items_meal_id_position",
                schema: "nutrition",
                table: "meal_items",
                columns: new[] { "meal_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_meal_items_tenant_id_meal_id",
                schema: "nutrition",
                table: "meal_items",
                columns: new[] { "tenant_id", "meal_id" });

            migrationBuilder.CreateIndex(
                name: "ix_meal_logs_member_id_meal_id_logged_on",
                schema: "nutrition",
                table: "meal_logs",
                columns: new[] { "member_id", "meal_id", "logged_on" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_meal_logs_tenant_id_assignment_id",
                schema: "nutrition",
                table: "meal_logs",
                columns: new[] { "tenant_id", "assignment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_meal_logs_tenant_id_meal_id",
                schema: "nutrition",
                table: "meal_logs",
                columns: new[] { "tenant_id", "meal_id" });

            migrationBuilder.CreateIndex(
                name: "ix_meal_logs_tenant_id_member_id_logged_on",
                schema: "nutrition",
                table: "meal_logs",
                columns: new[] { "tenant_id", "member_id", "logged_on" });

            migrationBuilder.CreateIndex(
                name: "ix_meal_logs_tenant_id_plan_version_id",
                schema: "nutrition",
                table: "meal_logs",
                columns: new[] { "tenant_id", "plan_version_id" });

            migrationBuilder.CreateIndex(
                name: "ix_meals_tenant_id_version_id",
                schema: "nutrition",
                table: "meals",
                columns: new[] { "tenant_id", "version_id" });

            migrationBuilder.CreateIndex(
                name: "ix_meals_version_id_position",
                schema: "nutrition",
                table: "meals",
                columns: new[] { "version_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_nutrition_assignments_tenant_id_member_id_start_date",
                schema: "nutrition",
                table: "nutrition_assignments",
                columns: new[] { "tenant_id", "member_id", "start_date" });

            migrationBuilder.CreateIndex(
                name: "ix_nutrition_assignments_tenant_id_plan_id",
                schema: "nutrition",
                table: "nutrition_assignments",
                columns: new[] { "tenant_id", "plan_id" });

            migrationBuilder.CreateIndex(
                name: "ix_nutrition_assignments_tenant_id_plan_version_id",
                schema: "nutrition",
                table: "nutrition_assignments",
                columns: new[] { "tenant_id", "plan_version_id" });

            migrationBuilder.CreateIndex(
                name: "ux_nutrition_assignments_open_member",
                schema: "nutrition",
                table: "nutrition_assignments",
                column: "member_id",
                unique: true,
                filter: "ended_on IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_plan_versions_plan_id_number",
                schema: "nutrition",
                table: "plan_versions",
                columns: new[] { "plan_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_plan_versions_tenant_id_plan_id",
                schema: "nutrition",
                table: "plan_versions",
                columns: new[] { "tenant_id", "plan_id" });

            migrationBuilder.CreateIndex(
                name: "ix_plans_tenant_id_status",
                schema: "nutrition",
                table: "plans",
                columns: new[] { "tenant_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dietary_profiles",
                schema: "nutrition");

            migrationBuilder.DropTable(
                name: "meal_items",
                schema: "nutrition");

            migrationBuilder.DropTable(
                name: "meal_logs",
                schema: "nutrition");

            migrationBuilder.DropTable(
                name: "nutrition_assignments",
                schema: "nutrition");

            migrationBuilder.DropTable(
                name: "meals",
                schema: "nutrition");

            migrationBuilder.DropTable(
                name: "plan_versions",
                schema: "nutrition");

            migrationBuilder.DropTable(
                name: "plans",
                schema: "nutrition");

            migrationBuilder.RenameIndex(
                name: "ix_plans_tenant_id_status1",
                schema: "training",
                table: "plans",
                newName: "ix_plans_tenant_id_status");

            migrationBuilder.RenameIndex(
                name: "ix_plan_versions_tenant_id_plan_id1",
                schema: "training",
                table: "plan_versions",
                newName: "ix_plan_versions_tenant_id_plan_id");

            migrationBuilder.RenameIndex(
                name: "ix_plan_versions_plan_id_number1",
                schema: "training",
                table: "plan_versions",
                newName: "ix_plan_versions_plan_id_number");
        }
    }
}
