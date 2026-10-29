using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Commercial.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMembershipRenewalIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_memberships_tenant_id_renewed_from_id",
                schema: "members",
                table: "memberships",
                columns: new[] { "tenant_id", "renewed_from_id" },
                filter: "renewed_from_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_memberships_tenant_id_renewed_from_id",
                schema: "members",
                table: "memberships");
        }
    }
}
