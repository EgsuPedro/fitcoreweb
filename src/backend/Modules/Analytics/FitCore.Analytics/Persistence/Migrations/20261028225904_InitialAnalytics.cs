using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Analytics.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialAnalytics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "analytics");

            migrationBuilder.CreateTable(
                name: "report_exports",
                schema: "analytics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    report = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    format = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    filters = table.Column<string>(type: "jsonb", nullable: false),
                    row_count = table.Column<int>(type: "integer", nullable: false),
                    file_size = table.Column<long>(type: "bigint", nullable: false),
                    file_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    download_count = table.Column<int>(type: "integer", nullable: false),
                    last_downloaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    purged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_report_exports", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_report_exports_tenant_id_expires_at",
                schema: "analytics",
                table: "report_exports",
                columns: new[] { "tenant_id", "expires_at" },
                filter: "file_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_report_exports_tenant_id_requested_by_requested_at",
                schema: "analytics",
                table: "report_exports",
                columns: new[] { "tenant_id", "requested_by", "requested_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "report_exports",
                schema: "analytics");
        }
    }
}
