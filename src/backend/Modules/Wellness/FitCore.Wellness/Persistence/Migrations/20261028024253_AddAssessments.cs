using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Wellness.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAssessments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "wellness");

            migrationBuilder.CreateTable(
                name: "body_assessments",
                schema: "wellness",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assessed_on = table.Column<DateOnly>(type: "date", nullable: false),
                    weight_kg = table.Column<decimal>(type: "numeric(5,1)", precision: 5, scale: 1, nullable: true),
                    height_cm = table.Column<decimal>(type: "numeric(4,1)", precision: 4, scale: 1, nullable: true),
                    waist_cm = table.Column<decimal>(type: "numeric(4,1)", precision: 4, scale: 1, nullable: true),
                    hip_cm = table.Column<decimal>(type: "numeric(4,1)", precision: 4, scale: 1, nullable: true),
                    chest_cm = table.Column<decimal>(type: "numeric(4,1)", precision: 4, scale: 1, nullable: true),
                    arm_cm = table.Column<decimal>(type: "numeric(4,1)", precision: 4, scale: 1, nullable: true),
                    thigh_cm = table.Column<decimal>(type: "numeric(4,1)", precision: 4, scale: 1, nullable: true),
                    body_fat_percent = table.Column<decimal>(type: "numeric(4,1)", precision: 4, scale: 1, nullable: true),
                    body_fat_method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    corrects_id = table.Column<Guid>(type: "uuid", nullable: true),
                    recorded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_body_assessments", x => x.id);
                    table.UniqueConstraint("ak_body_assessments_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_body_assessments_body_assessments_tenant_id_corrects_id",
                        columns: x => new { x.tenant_id, x.corrects_id },
                        principalSchema: "wellness",
                        principalTable: "body_assessments",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_body_assessments_tenant_id_corrects_id",
                schema: "wellness",
                table: "body_assessments",
                columns: new[] { "tenant_id", "corrects_id" });

            migrationBuilder.CreateIndex(
                name: "ix_body_assessments_tenant_id_member_id_assessed_on",
                schema: "wellness",
                table: "body_assessments",
                columns: new[] { "tenant_id", "member_id", "assessed_on" });

            migrationBuilder.CreateIndex(
                name: "ux_body_assessments_corrects",
                schema: "wellness",
                table: "body_assessments",
                column: "corrects_id",
                unique: true,
                filter: "corrects_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "body_assessments",
                schema: "wellness");
        }
    }
}
