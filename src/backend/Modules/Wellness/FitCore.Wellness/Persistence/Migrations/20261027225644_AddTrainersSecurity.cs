using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Wellness.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTrainersSecurity : Migration
    {
        private static readonly string[] TenantTables =
        [
            "training.trainer_profiles",
            "training.trainer_availability",
            "training.trainer_assignments",
            "training.trainer_notes"
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantTables)
            {
                TenantTableSecurity.AddTenantForeignKey(migrationBuilder, table, $"fk_{table[(table.IndexOf('.') + 1)..]}_tenant");
            }

            TenantTableSecurity.AddBranchForeignKey(migrationBuilder, "training.trainer_assignments", "fk_trainer_assignments_branch");
            TenantTableSecurity.AddBranchForeignKey(migrationBuilder, "training.trainer_availability", "fk_trainer_availability_branch");
            TenantTableSecurity.EnableRowLevelSecurity(migrationBuilder, TenantTables);
            TenantTableSecurity.GrantApplicationRole(migrationBuilder, "training");

            migrationBuilder.Sql("""
                -- El perfil de entrenador es de un usuario, y las asignaciones y observaciones son de un alumno del mismo gimnasio.
                ALTER TABLE training.trainer_profiles ADD CONSTRAINT fk_trainer_profiles_user
                  FOREIGN KEY (user_id) REFERENCES identity.app_users (id);
                ALTER TABLE training.trainer_assignments ADD CONSTRAINT fk_trainer_assignments_member
                  FOREIGN KEY (tenant_id, member_id) REFERENCES members.members (tenant_id, id);
                ALTER TABLE training.trainer_notes ADD CONSTRAINT fk_trainer_notes_member
                  FOREIGN KEY (tenant_id, member_id) REFERENCES members.members (tenant_id, id);

                ALTER TABLE training.trainer_profiles
                  ADD CONSTRAINT ck_trainer_profiles_status CHECK (status IN ('Active', 'Inactive')),
                  ADD CONSTRAINT ck_trainer_profiles_specialties CHECK (cardinality(specialties) <= 10);

                ALTER TABLE training.trainer_availability
                  ADD CONSTRAINT ck_trainer_availability_day CHECK (day BETWEEN 0 AND 6),
                  ADD CONSTRAINT ck_trainer_availability_range CHECK (end_time > start_time);

                ALTER TABLE training.trainer_assignments
                  ADD CONSTRAINT ck_trainer_assignments_ended CHECK (
                    (ended_on IS NULL) = (end_reason IS NULL)
                    AND (ended_on IS NULL OR ended_on >= assigned_on));

                ALTER TABLE training.trainer_notes
                  ADD CONSTRAINT ck_trainer_notes_text CHECK (length(btrim(text)) > 0);

                -- Una asignación solo cambia al cerrarse; una vez cerrada es definitiva y sus datos nunca se reescriben.
                CREATE FUNCTION training.guard_assignment_update() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF OLD.ended_on IS NOT NULL THEN
                    RAISE EXCEPTION 'Una asignación cerrada no se puede modificar' USING ERRCODE = '23514';
                  END IF;
                  IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id OR NEW.trainer_id IS DISTINCT FROM OLD.trainer_id
                     OR NEW.member_id IS DISTINCT FROM OLD.member_id OR NEW.branch_id IS DISTINCT FROM OLD.branch_id
                     OR NEW.assigned_on IS DISTINCT FROM OLD.assigned_on OR NEW.assigned_by IS DISTINCT FROM OLD.assigned_by THEN
                    RAISE EXCEPTION 'Los datos de la asignación no se pueden modificar' USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_trainer_assignments_guard_update BEFORE UPDATE ON training.trainer_assignments
                  FOR EACH ROW EXECUTE FUNCTION training.guard_assignment_update();

                -- Las observaciones solo se agregan: ni siquiera quien administra la base las edita o borra por descuido.
                CREATE FUNCTION training.forbid_note_change() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  RAISE EXCEPTION 'Las observaciones del entrenador no se pueden modificar ni borrar' USING ERRCODE = '23514';
                END
                $fn$;
                CREATE TRIGGER trg_trainer_notes_immutable BEFORE UPDATE OR DELETE ON training.trainer_notes
                  FOR EACH ROW EXECUTE FUNCTION training.forbid_note_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_trainer_notes_immutable ON training.trainer_notes;
                DROP FUNCTION IF EXISTS training.forbid_note_change();
                DROP TRIGGER IF EXISTS trg_trainer_assignments_guard_update ON training.trainer_assignments;
                DROP FUNCTION IF EXISTS training.guard_assignment_update();
                ALTER TABLE training.trainer_notes DROP CONSTRAINT IF EXISTS ck_trainer_notes_text, DROP CONSTRAINT IF EXISTS fk_trainer_notes_member;
                ALTER TABLE training.trainer_assignments DROP CONSTRAINT IF EXISTS ck_trainer_assignments_ended, DROP CONSTRAINT IF EXISTS fk_trainer_assignments_member;
                ALTER TABLE training.trainer_availability DROP CONSTRAINT IF EXISTS ck_trainer_availability_day, DROP CONSTRAINT IF EXISTS ck_trainer_availability_range;
                ALTER TABLE training.trainer_profiles DROP CONSTRAINT IF EXISTS ck_trainer_profiles_status, DROP CONSTRAINT IF EXISTS ck_trainer_profiles_specialties, DROP CONSTRAINT IF EXISTS fk_trainer_profiles_user;
                """);

            TenantTableSecurity.DisableRowLevelSecurity(migrationBuilder, TenantTables);
            migrationBuilder.Sql("ALTER TABLE training.trainer_availability DROP CONSTRAINT IF EXISTS fk_trainer_availability_branch;");
            migrationBuilder.Sql("ALTER TABLE training.trainer_assignments DROP CONSTRAINT IF EXISTS fk_trainer_assignments_branch;");
            foreach (var table in TenantTables)
            {
                migrationBuilder.Sql($"ALTER TABLE {table} DROP CONSTRAINT IF EXISTS fk_{table[(table.IndexOf('.') + 1)..]}_tenant;");
            }
        }
    }
}
