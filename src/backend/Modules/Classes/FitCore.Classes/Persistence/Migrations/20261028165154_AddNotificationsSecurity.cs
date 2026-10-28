using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Classes.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationsSecurity : Migration
    {
        private static readonly string[] TenantTables =
        [
            "classes.inbox_notifications",
            "classes.notification_preferences"
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            TenantTableSecurity.AddTenantForeignKey(migrationBuilder, "classes.inbox_notifications", "fk_inbox_notifications_tenant");
            TenantTableSecurity.AddTenantForeignKey(migrationBuilder, "classes.notification_preferences", "fk_notification_preferences_tenant");
            TenantTableSecurity.AddBranchForeignKey(migrationBuilder, "classes.inbox_notifications", "fk_inbox_notifications_branch");
            TenantTableSecurity.EnableRowLevelSecurity(migrationBuilder, TenantTables);
            TenantTableSecurity.GrantApplicationRole(migrationBuilder, "classes");

            migrationBuilder.Sql("""
                -- El destinatario es un usuario del mismo gimnasio: la base rechaza el de otro.
                ALTER TABLE classes.inbox_notifications
                  ADD CONSTRAINT fk_inbox_notifications_user FOREIGN KEY (tenant_id, recipient_user_id) REFERENCES identity.app_users (tenant_id, id);
                ALTER TABLE classes.notification_preferences
                  ADD CONSTRAINT fk_notification_preferences_user FOREIGN KEY (tenant_id, user_id) REFERENCES identity.app_users (tenant_id, id);

                ALTER TABLE classes.inbox_notifications
                  ADD CONSTRAINT ck_inbox_notifications_type CHECK (type IN (
                    'ReservationConfirmed', 'WaitlistJoined', 'WaitlistOffer', 'WaitlistOfferExpired', 'ReservationCancelledByGym',
                    'SessionCancelled', 'SessionRescheduled', 'ClassReminder')),
                  ADD CONSTRAINT ck_inbox_notifications_channel CHECK (channel IN ('InApp', 'Email', 'Sms', 'WhatsApp', 'Push')),
                  ADD CONSTRAINT ck_inbox_notifications_status CHECK (status IN ('Pending', 'Delivered', 'Failed')),
                  ADD CONSTRAINT ck_inbox_notifications_delivered CHECK ((status = 'Delivered') = (delivered_at IS NOT NULL)),
                  ADD CONSTRAINT ck_inbox_notifications_pending CHECK ((status = 'Pending') = (next_attempt_at IS NOT NULL)),
                  ADD CONSTRAINT ck_inbox_notifications_attempts CHECK (attempts BETWEEN 0 AND 10),
                  ADD CONSTRAINT ck_inbox_notifications_read CHECK (read_at IS NULL OR status = 'Delivered'),
                  ADD CONSTRAINT ck_inbox_notifications_text CHECK (length(btrim(title)) > 0 AND length(btrim(body)) > 0 AND length(btrim(dedupe_key)) > 0);

                ALTER TABLE classes.notification_preferences
                  ADD CONSTRAINT ck_notification_preferences_type CHECK (type IN (
                    'ReservationConfirmed', 'WaitlistJoined', 'WaitlistOffer', 'WaitlistOfferExpired', 'ReservationCancelledByGym',
                    'SessionCancelled', 'SessionRescheduled', 'ClassReminder'));

                -- Lo que se avisó no cambia: solo avanza su entrega (estado, intentos, error) y se marca como leído, una sola vez.
                CREATE FUNCTION classes.guard_inbox_notification_update() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id OR NEW.recipient_user_id IS DISTINCT FROM OLD.recipient_user_id
                     OR NEW.branch_id IS DISTINCT FROM OLD.branch_id OR NEW.type IS DISTINCT FROM OLD.type
                     OR NEW.title IS DISTINCT FROM OLD.title OR NEW.body IS DISTINCT FROM OLD.body OR NEW.data IS DISTINCT FROM OLD.data
                     OR NEW.dedupe_key IS DISTINCT FROM OLD.dedupe_key OR NEW.channel IS DISTINCT FROM OLD.channel
                     OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
                    RAISE EXCEPTION 'El contenido y el destinatario de un aviso no se pueden modificar' USING ERRCODE = '23514';
                  END IF;
                  IF OLD.read_at IS NOT NULL AND NEW.read_at IS DISTINCT FROM OLD.read_at THEN
                    RAISE EXCEPTION 'Un aviso leído no vuelve a no leído' USING ERRCODE = '23514';
                  END IF;
                  IF OLD.status IN ('Delivered', 'Failed') AND NEW.status IS DISTINCT FROM OLD.status THEN
                    RAISE EXCEPTION 'Un aviso entregado o fallido no cambia de estado' USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_inbox_notifications_guard_update BEFORE UPDATE ON classes.inbox_notifications
                  FOR EACH ROW EXECUTE FUNCTION classes.guard_inbox_notification_update();

                -- La bandeja de un usuario, lo más reciente primero; y los pendientes de entrega que busca el Worker.
                CREATE INDEX ix_inbox_notifications_unread ON classes.inbox_notifications (tenant_id, recipient_user_id, created_at DESC) WHERE read_at IS NULL;
                CREATE INDEX ix_inbox_notifications_due ON classes.inbox_notifications (next_attempt_at) WHERE status = 'Pending';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS classes.ix_inbox_notifications_due;
                DROP INDEX IF EXISTS classes.ix_inbox_notifications_unread;
                DROP TRIGGER IF EXISTS trg_inbox_notifications_guard_update ON classes.inbox_notifications;
                DROP FUNCTION IF EXISTS classes.guard_inbox_notification_update();
                ALTER TABLE classes.notification_preferences DROP CONSTRAINT IF EXISTS fk_notification_preferences_user;
                ALTER TABLE classes.inbox_notifications DROP CONSTRAINT IF EXISTS fk_inbox_notifications_user;
                """);
            TenantTableSecurity.DisableRowLevelSecurity(migrationBuilder, TenantTables);
            migrationBuilder.Sql("""
                ALTER TABLE classes.inbox_notifications DROP CONSTRAINT IF EXISTS fk_inbox_notifications_branch;
                ALTER TABLE classes.inbox_notifications DROP CONSTRAINT IF EXISTS fk_inbox_notifications_tenant;
                ALTER TABLE classes.notification_preferences DROP CONSTRAINT IF EXISTS fk_notification_preferences_tenant;
                """);
        }
    }
}
