using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Commercial.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationsSecurity : Migration
    {
        private static readonly string[] TenantTables = ["members.notifications"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            TenantTableSecurity.AddTenantForeignKey(migrationBuilder, "members.notifications", "fk_notifications_tenant");
            TenantTableSecurity.AddBranchForeignKey(migrationBuilder, "members.notifications", "fk_notifications_branch");
            TenantTableSecurity.EnableRowLevelSecurity(migrationBuilder, TenantTables);
            TenantTableSecurity.GrantApplicationRole(migrationBuilder, "members", "billing", "cash", "access");

            migrationBuilder.Sql("""
                ALTER TABLE members.notifications
                  ADD CONSTRAINT ck_notifications_type CHECK (type IN ('StaffExpiringSoon', 'MemberDueDay', 'MemberAfterDue')),
                  ADD CONSTRAINT ck_notifications_kind CHECK (recipient_kind IN ('Staff', 'Member')),
                  ADD CONSTRAINT ck_notifications_status CHECK (status IN ('Pending', 'Sent', 'Failed', 'Sending')),
                  ADD CONSTRAINT ck_notifications_attempts CHECK (attempts >= 0),
                  ADD CONSTRAINT ck_notifications_sent CHECK ((status = 'Sent') = (sent_at IS NOT NULL));

                -- Un teléfono solo puede quedar guardado con el formato internacional que exige WhatsApp.
                ALTER TABLE members.notifications
                  ADD CONSTRAINT ck_notifications_recipient CHECK (recipient ~ '^\+[0-9]{8,15}$');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE members.notifications
                  DROP CONSTRAINT IF EXISTS ck_notifications_type, DROP CONSTRAINT IF EXISTS ck_notifications_kind,
                  DROP CONSTRAINT IF EXISTS ck_notifications_status, DROP CONSTRAINT IF EXISTS ck_notifications_attempts,
                  DROP CONSTRAINT IF EXISTS ck_notifications_sent, DROP CONSTRAINT IF EXISTS ck_notifications_recipient;
                """);

            TenantTableSecurity.DisableRowLevelSecurity(migrationBuilder, TenantTables);
            migrationBuilder.Sql("ALTER TABLE members.notifications DROP CONSTRAINT IF EXISTS fk_notifications_branch;");
            migrationBuilder.Sql("ALTER TABLE members.notifications DROP CONSTRAINT IF EXISTS fk_notifications_tenant;");
        }
    }
}
