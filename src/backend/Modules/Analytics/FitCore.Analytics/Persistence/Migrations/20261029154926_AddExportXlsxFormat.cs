using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Analytics.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExportXlsxFormat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Además de CSV, las exportaciones pueden ser un libro de Excel (con la marca del gimnasio). La lista sigue cerrada.
            migrationBuilder.Sql("""
                ALTER TABLE analytics.report_exports DROP CONSTRAINT ck_report_exports_format;
                ALTER TABLE analytics.report_exports ADD CONSTRAINT ck_report_exports_format CHECK (format IN ('csv', 'xlsx'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE analytics.report_exports DROP CONSTRAINT ck_report_exports_format;
                ALTER TABLE analytics.report_exports ADD CONSTRAINT ck_report_exports_format CHECK (format = 'csv');
                """);
        }
    }
}
