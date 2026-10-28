using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Wellness.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPhotos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "photo_consents",
                schema: "wellness",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    source = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    notice_version = table.Column<int>(type: "integer", nullable: false),
                    guardian_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    recorded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_photo_consents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "progress_photos",
                schema: "wellness",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    taken_on = table.Column<DateOnly>(type: "date", nullable: false),
                    angle = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    content_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    consent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_progress_photos", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_photo_consents_tenant_id_member_id_recorded_at",
                schema: "wellness",
                table: "photo_consents",
                columns: new[] { "tenant_id", "member_id", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "ix_progress_photos_storage_key",
                schema: "wellness",
                table: "progress_photos",
                column: "storage_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_progress_photos_tenant_id_member_id_taken_on",
                schema: "wellness",
                table: "progress_photos",
                columns: new[] { "tenant_id", "member_id", "taken_on" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "photo_consents",
                schema: "wellness");

            migrationBuilder.DropTable(
                name: "progress_photos",
                schema: "wellness");
        }
    }
}
