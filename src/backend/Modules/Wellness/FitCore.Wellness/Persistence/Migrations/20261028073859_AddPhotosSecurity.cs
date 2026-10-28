using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Wellness.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPhotosSecurity : Migration
    {
        private static readonly string[] TenantTables = ["wellness.photo_consents", "wellness.progress_photos"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            TenantTableSecurity.AddTenantForeignKey(migrationBuilder, "wellness.photo_consents", "fk_photo_consents_tenant");
            TenantTableSecurity.AddTenantForeignKey(migrationBuilder, "wellness.progress_photos", "fk_progress_photos_tenant");
            TenantTableSecurity.EnableRowLevelSecurity(migrationBuilder, TenantTables);
            TenantTableSecurity.GrantApplicationRole(migrationBuilder, "wellness");

            migrationBuilder.Sql("""
                ALTER TABLE wellness.photo_consents ADD CONSTRAINT uq_photo_consents_tenant_id UNIQUE (tenant_id, id);
                ALTER TABLE wellness.photo_consents ADD CONSTRAINT fk_photo_consents_member
                  FOREIGN KEY (tenant_id, member_id) REFERENCES members.members (tenant_id, id);
                ALTER TABLE wellness.photo_consents ADD CONSTRAINT fk_photo_consents_recorded_by
                  FOREIGN KEY (recorded_by) REFERENCES identity.app_users (id);
                ALTER TABLE wellness.photo_consents
                  ADD CONSTRAINT ck_photo_consents_action CHECK (action IN ('Granted', 'Revoked')),
                  ADD CONSTRAINT ck_photo_consents_source CHECK (source IN ('Member', 'Staff')),
                  ADD CONSTRAINT ck_photo_consents_notice CHECK (notice_version >= 1),
                  -- El representante solo aplica a un consentimiento dado (un retiro no lo lleva) y debe tener un nombre real.
                  ADD CONSTRAINT ck_photo_consents_guardian CHECK (
                    guardian_name IS NULL OR (action = 'Granted' AND char_length(btrim(guardian_name)) >= 3));

                ALTER TABLE wellness.progress_photos ADD CONSTRAINT fk_progress_photos_member
                  FOREIGN KEY (tenant_id, member_id) REFERENCES members.members (tenant_id, id);
                ALTER TABLE wellness.progress_photos ADD CONSTRAINT fk_progress_photos_consent
                  FOREIGN KEY (tenant_id, consent_id) REFERENCES wellness.photo_consents (tenant_id, id);
                ALTER TABLE wellness.progress_photos ADD CONSTRAINT fk_progress_photos_uploaded_by
                  FOREIGN KEY (uploaded_by) REFERENCES identity.app_users (id);
                ALTER TABLE wellness.progress_photos
                  ADD CONSTRAINT ck_progress_photos_angle CHECK (angle IN ('Front', 'Side', 'Back', 'Other')),
                  ADD CONSTRAINT ck_progress_photos_content_type CHECK (content_type IN ('image/jpeg', 'image/png')),
                  ADD CONSTRAINT ck_progress_photos_size CHECK (size_bytes BETWEEN 1 AND 8388608),
                  -- La clave del archivo es aleatoria: sin nombres, fechas ni identificadores de la persona.
                  ADD CONSTRAINT ck_progress_photos_key CHECK (storage_key ~ '^progress-photos/[0-9a-f]{32}/[0-9a-f]{32}$');

                -- Los consentimientos son solo-agregar. Cada alta toma un candado por alumno que dura hasta confirmar la transacción:
                -- sirve para que retirar el consentimiento y subir una foto no se crucen (ver guard_progress_photo).
                CREATE FUNCTION wellness.guard_photo_consent() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF TG_OP = 'INSERT' THEN
                    PERFORM pg_advisory_xact_lock(hashtextextended(NEW.member_id::text, 0));
                    RETURN NEW;
                  END IF;
                  RAISE EXCEPTION 'Los registros de consentimiento no se pueden modificar ni borrar' USING ERRCODE = '23514';
                END
                $fn$;
                CREATE TRIGGER trg_photo_consents_guard BEFORE INSERT OR UPDATE OR DELETE ON wellness.photo_consents
                  FOR EACH ROW EXECUTE FUNCTION wellness.guard_photo_consent();

                -- Una foto solo entra si el último consentimiento del alumno es un "dado" y es el mismo que la foto declara. Toma el mismo
                -- candado: si hay un retiro en curso, espera a que confirme y entonces ve el consentimiento ya retirado. Las fotos no se editan.
                CREATE FUNCTION wellness.guard_progress_photo() RETURNS trigger LANGUAGE plpgsql AS $fn$
                DECLARE latest record;
                BEGIN
                  IF TG_OP = 'UPDATE' THEN
                    RAISE EXCEPTION 'Las fotos no se pueden modificar' USING ERRCODE = '23514';
                  END IF;
                  PERFORM pg_advisory_xact_lock(hashtextextended(NEW.member_id::text, 0));
                  SELECT id, action INTO latest FROM wellness.photo_consents
                    WHERE member_id = NEW.member_id ORDER BY recorded_at DESC, id DESC LIMIT 1;
                  IF NOT FOUND OR latest.action <> 'Granted' OR latest.id <> NEW.consent_id THEN
                    RAISE EXCEPTION 'No hay un consentimiento vigente para guardar fotos' USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_progress_photos_guard BEFORE INSERT OR UPDATE ON wellness.progress_photos
                  FOR EACH ROW EXECUTE FUNCTION wellness.guard_progress_photo();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_progress_photos_guard ON wellness.progress_photos;
                DROP FUNCTION IF EXISTS wellness.guard_progress_photo();
                DROP TRIGGER IF EXISTS trg_photo_consents_guard ON wellness.photo_consents;
                DROP FUNCTION IF EXISTS wellness.guard_photo_consent();
                ALTER TABLE wellness.progress_photos
                  DROP CONSTRAINT IF EXISTS fk_progress_photos_uploaded_by,
                  DROP CONSTRAINT IF EXISTS fk_progress_photos_consent,
                  DROP CONSTRAINT IF EXISTS fk_progress_photos_member;
                ALTER TABLE wellness.photo_consents
                  DROP CONSTRAINT IF EXISTS fk_photo_consents_recorded_by,
                  DROP CONSTRAINT IF EXISTS fk_photo_consents_member;
                """);
            TenantTableSecurity.DisableRowLevelSecurity(migrationBuilder, TenantTables);
            migrationBuilder.Sql("ALTER TABLE wellness.progress_photos DROP CONSTRAINT IF EXISTS fk_progress_photos_tenant;");
            migrationBuilder.Sql("ALTER TABLE wellness.photo_consents DROP CONSTRAINT IF EXISTS fk_photo_consents_tenant;");
        }
    }
}
