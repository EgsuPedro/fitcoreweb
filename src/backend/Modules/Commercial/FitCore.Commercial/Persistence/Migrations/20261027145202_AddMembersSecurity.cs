using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Commercial.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMembersSecurity : Migration
    {
        private static readonly string[] TenantTables = ["members.members", "members.counters"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            TenantTableSecurity.AddTenantForeignKey(migrationBuilder, "members.members", "fk_members_tenant");
            TenantTableSecurity.AddTenantForeignKey(migrationBuilder, "members.counters", "fk_counters_tenant");
            TenantTableSecurity.AddBranchForeignKey(migrationBuilder, "members.members", "fk_members_branch");
            TenantTableSecurity.EnableRowLevelSecurity(migrationBuilder, TenantTables);
            TenantTableSecurity.GrantApplicationRole(migrationBuilder, "members");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            TenantTableSecurity.DisableRowLevelSecurity(migrationBuilder, TenantTables);
            migrationBuilder.Sql("ALTER TABLE members.members DROP CONSTRAINT IF EXISTS fk_members_branch;");
            migrationBuilder.Sql("ALTER TABLE members.counters DROP CONSTRAINT IF EXISTS fk_counters_tenant;");
            migrationBuilder.Sql("ALTER TABLE members.members DROP CONSTRAINT IF EXISTS fk_members_tenant;");
        }
    }
}
