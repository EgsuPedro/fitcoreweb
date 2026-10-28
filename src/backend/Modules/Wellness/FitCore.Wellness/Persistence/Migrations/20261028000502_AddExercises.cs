using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Wellness.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExercises : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "exercises",
                schema: "training",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    primary_muscle = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    secondary_muscles = table.Column<string[]>(type: "text[]", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    equipment = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    difficulty = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    tracking_mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    instructions = table.Column<string[]>(type: "text[]", nullable: false),
                    common_mistakes = table.Column<string[]>(type: "text[]", nullable: false),
                    warnings = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    video_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_exercises", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_exercises_tenant_id_is_active",
                schema: "training",
                table: "exercises",
                columns: new[] { "tenant_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_exercises_tenant_id_primary_muscle",
                schema: "training",
                table: "exercises",
                columns: new[] { "tenant_id", "primary_muscle" });

            migrationBuilder.CreateIndex(
                name: "ux_exercises_global_name",
                schema: "training",
                table: "exercises",
                column: "name",
                unique: true,
                filter: "tenant_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_exercises_tenant_name",
                schema: "training",
                table: "exercises",
                columns: new[] { "tenant_id", "name" },
                unique: true,
                filter: "tenant_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "exercises",
                schema: "training");
        }
    }
}
