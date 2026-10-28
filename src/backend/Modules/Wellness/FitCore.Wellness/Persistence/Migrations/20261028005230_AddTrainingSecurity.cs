using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Wellness.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTrainingSecurity : Migration
    {
        private static readonly string[] TenantTables =
        [
            "training.plans",
            "training.plan_versions",
            "training.plan_days",
            "training.plan_exercises",
            "training.training_assignments",
            "training.workout_sessions",
            "training.workout_session_exercises",
            "training.workout_sets"
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantTables)
            {
                TenantTableSecurity.AddTenantForeignKey(migrationBuilder, table, $"fk_{table[(table.IndexOf('.') + 1)..]}_tenant");
            }

            TenantTableSecurity.AddBranchForeignKey(migrationBuilder, "training.training_assignments", "fk_training_assignments_branch");
            TenantTableSecurity.EnableRowLevelSecurity(migrationBuilder, TenantTables);
            TenantTableSecurity.GrantApplicationRole(migrationBuilder, "training");

            migrationBuilder.Sql("""
                -- Los alumnos y quienes asignan o registran pertenecen al mismo gimnasio.
                ALTER TABLE training.training_assignments ADD CONSTRAINT fk_training_assignments_member
                  FOREIGN KEY (tenant_id, member_id) REFERENCES members.members (tenant_id, id);
                ALTER TABLE training.training_assignments ADD CONSTRAINT fk_training_assignments_assigned_by
                  FOREIGN KEY (assigned_by) REFERENCES identity.app_users (id);
                ALTER TABLE training.workout_sessions ADD CONSTRAINT fk_workout_sessions_member
                  FOREIGN KEY (tenant_id, member_id) REFERENCES members.members (tenant_id, id);
                ALTER TABLE training.workout_sessions ADD CONSTRAINT fk_workout_sessions_recorded_by
                  FOREIGN KEY (recorded_by) REFERENCES identity.app_users (id);

                -- La cabecera apunta a su versión vigente; ambas se crean en la misma transacción, por eso la referencia se comprueba al final.
                ALTER TABLE training.plans ADD CONSTRAINT fk_plans_current_version
                  FOREIGN KEY (current_version_id) REFERENCES training.plan_versions (id) DEFERRABLE INITIALLY DEFERRED;

                ALTER TABLE training.plans
                  ADD CONSTRAINT ck_plans_status CHECK (status IN ('Active', 'Archived')),
                  ADD CONSTRAINT ck_plans_version CHECK (current_version >= 0);

                ALTER TABLE training.plan_versions
                  ADD CONSTRAINT ck_plan_versions_level CHECK (level IN ('Beginner', 'Intermediate', 'Advanced')),
                  ADD CONSTRAINT ck_plan_versions_minutes CHECK (estimated_minutes IS NULL OR estimated_minutes BETWEEN 5 AND 480),
                  ADD CONSTRAINT ck_plan_versions_validity CHECK (valid_from IS NULL OR valid_until IS NULL OR valid_until >= valid_from);

                -- La prescripción debe ser coherente con cómo se registra el ejercicio (repeticiones y carga, repeticiones, o tiempo).
                ALTER TABLE training.plan_exercises
                  ADD CONSTRAINT ck_plan_exercises_sets CHECK (sets BETWEEN 1 AND 20),
                  ADD CONSTRAINT ck_plan_exercises_rest CHECK (rest_seconds BETWEEN 0 AND 1800),
                  ADD CONSTRAINT ck_plan_exercises_load CHECK (recommended_load_kg IS NULL OR recommended_load_kg BETWEEN 0 AND 1000),
                  ADD CONSTRAINT ck_plan_exercises_mode CHECK (
                    (tracking_mode = 'RepsAndLoad' AND reps BETWEEN 1 AND 200 AND duration_seconds IS NULL)
                    OR (tracking_mode = 'Reps' AND reps BETWEEN 1 AND 200 AND duration_seconds IS NULL AND recommended_load_kg IS NULL)
                    OR (tracking_mode = 'Duration' AND duration_seconds BETWEEN 5 AND 14400 AND reps IS NULL AND recommended_load_kg IS NULL));

                ALTER TABLE training.training_assignments
                  ADD CONSTRAINT ck_training_assignments_range CHECK (end_date IS NULL OR end_date >= start_date),
                  ADD CONSTRAINT ck_training_assignments_ended CHECK (
                    (ended_on IS NULL) = (end_reason IS NULL) AND (ended_on IS NULL OR ended_on >= start_date - 1));

                ALTER TABLE training.workout_sessions
                  ADD CONSTRAINT ck_workout_sessions_duration CHECK (duration_minutes IS NULL OR duration_minutes BETWEEN 1 AND 600),
                  ADD CONSTRAINT ck_workout_sessions_comment CHECK ((trainer_comment IS NULL) = (trainer_comment_at IS NULL));

                ALTER TABLE training.workout_session_exercises
                  ADD CONSTRAINT ck_workout_session_exercises_mode CHECK (tracking_mode IN ('RepsAndLoad', 'Reps', 'Duration'));

                ALTER TABLE training.workout_sets
                  ADD CONSTRAINT ck_workout_sets_number CHECK (set_number BETWEEN 1 AND 20),
                  ADD CONSTRAINT ck_workout_sets_measure CHECK ((reps IS NOT NULL) <> (duration_seconds IS NOT NULL)),
                  ADD CONSTRAINT ck_workout_sets_reps CHECK (reps IS NULL OR reps BETWEEN 1 AND 200),
                  ADD CONSTRAINT ck_workout_sets_duration CHECK (duration_seconds IS NULL OR duration_seconds BETWEEN 1 AND 14400),
                  ADD CONSTRAINT ck_workout_sets_load CHECK (load_kg IS NULL OR load_kg BETWEEN 0 AND 1000);

                -- Historial inmutable: versiones, días, ejercicios prescritos y lo realizado nunca se reescriben ni se borran.
                CREATE FUNCTION training.forbid_history_change() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  RAISE EXCEPTION 'El historial de entrenamiento no se puede modificar ni borrar' USING ERRCODE = '23514';
                END
                $fn$;
                CREATE TRIGGER trg_plan_versions_immutable BEFORE UPDATE OR DELETE ON training.plan_versions
                  FOR EACH ROW EXECUTE FUNCTION training.forbid_history_change();
                CREATE TRIGGER trg_plan_days_immutable BEFORE UPDATE OR DELETE ON training.plan_days
                  FOR EACH ROW EXECUTE FUNCTION training.forbid_history_change();
                CREATE TRIGGER trg_plan_exercises_immutable BEFORE UPDATE OR DELETE ON training.plan_exercises
                  FOR EACH ROW EXECUTE FUNCTION training.forbid_history_change();
                CREATE TRIGGER trg_workout_session_exercises_immutable BEFORE UPDATE OR DELETE ON training.workout_session_exercises
                  FOR EACH ROW EXECUTE FUNCTION training.forbid_history_change();
                CREATE TRIGGER trg_workout_sets_immutable BEFORE UPDATE OR DELETE ON training.workout_sets
                  FOR EACH ROW EXECUTE FUNCTION training.forbid_history_change();
                CREATE TRIGGER trg_plans_no_delete BEFORE DELETE ON training.plans
                  FOR EACH ROW EXECUTE FUNCTION training.forbid_history_change();
                CREATE TRIGGER trg_training_assignments_no_delete BEFORE DELETE ON training.training_assignments
                  FOR EACH ROW EXECUTE FUNCTION training.forbid_history_change();
                CREATE TRIGGER trg_workout_sessions_no_delete BEFORE DELETE ON training.workout_sessions
                  FOR EACH ROW EXECUTE FUNCTION training.forbid_history_change();

                -- La versión vigente de una rutina solo avanza, y la cabecera nunca cambia de gimnasio.
                CREATE FUNCTION training.guard_plan_update() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
                    RAISE EXCEPTION 'Los datos de origen de la rutina no se pueden modificar' USING ERRCODE = '23514';
                  END IF;
                  IF NEW.current_version < OLD.current_version THEN
                    RAISE EXCEPTION 'La versión de una rutina no puede retroceder' USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_plans_guard_update BEFORE UPDATE ON training.plans
                  FOR EACH ROW EXECUTE FUNCTION training.guard_plan_update();

                -- Una asignación solo cambia al cerrarse; cerrada es definitiva y sus datos nunca se reescriben.
                CREATE FUNCTION training.guard_training_assignment_update() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF OLD.ended_on IS NOT NULL THEN
                    RAISE EXCEPTION 'Una asignación cerrada no se puede modificar' USING ERRCODE = '23514';
                  END IF;
                  IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id OR NEW.plan_id IS DISTINCT FROM OLD.plan_id
                     OR NEW.plan_version_id IS DISTINCT FROM OLD.plan_version_id OR NEW.member_id IS DISTINCT FROM OLD.member_id
                     OR NEW.branch_id IS DISTINCT FROM OLD.branch_id OR NEW.start_date IS DISTINCT FROM OLD.start_date
                     OR NEW.end_date IS DISTINCT FROM OLD.end_date OR NEW.assigned_by IS DISTINCT FROM OLD.assigned_by THEN
                    RAISE EXCEPTION 'Los datos de la asignación no se pueden modificar' USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_training_assignments_guard_update BEFORE UPDATE ON training.training_assignments
                  FOR EACH ROW EXECUTE FUNCTION training.guard_training_assignment_update();

                -- Una sesión solo cambia cuando el entrenador agrega su comentario (una vez); lo que el alumno registró no se reescribe.
                CREATE FUNCTION training.guard_workout_session_update() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF OLD.trainer_comment IS NOT NULL THEN
                    RAISE EXCEPTION 'Una sesión con comentario del entrenador no se puede modificar' USING ERRCODE = '23514';
                  END IF;
                  IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id OR NEW.member_id IS DISTINCT FROM OLD.member_id
                     OR NEW.assignment_id IS DISTINCT FROM OLD.assignment_id OR NEW.plan_version_id IS DISTINCT FROM OLD.plan_version_id
                     OR NEW.day_id IS DISTINCT FROM OLD.day_id OR NEW.performed_on IS DISTINCT FROM OLD.performed_on
                     OR NEW.duration_minutes IS DISTINCT FROM OLD.duration_minutes OR NEW.member_notes IS DISTINCT FROM OLD.member_notes
                     OR NEW.recorded_by IS DISTINCT FROM OLD.recorded_by OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
                    RAISE EXCEPTION 'Lo que registró el alumno en la sesión no se puede modificar' USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_workout_sessions_guard_update BEFORE UPDATE ON training.workout_sessions
                  FOR EACH ROW EXECUTE FUNCTION training.guard_workout_session_update();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_workout_sessions_guard_update ON training.workout_sessions;
                DROP FUNCTION IF EXISTS training.guard_workout_session_update();
                DROP TRIGGER IF EXISTS trg_training_assignments_guard_update ON training.training_assignments;
                DROP FUNCTION IF EXISTS training.guard_training_assignment_update();
                DROP TRIGGER IF EXISTS trg_plans_guard_update ON training.plans;
                DROP FUNCTION IF EXISTS training.guard_plan_update();
                DROP TRIGGER IF EXISTS trg_workout_sessions_no_delete ON training.workout_sessions;
                DROP TRIGGER IF EXISTS trg_training_assignments_no_delete ON training.training_assignments;
                DROP TRIGGER IF EXISTS trg_plans_no_delete ON training.plans;
                DROP TRIGGER IF EXISTS trg_workout_sets_immutable ON training.workout_sets;
                DROP TRIGGER IF EXISTS trg_workout_session_exercises_immutable ON training.workout_session_exercises;
                DROP TRIGGER IF EXISTS trg_plan_exercises_immutable ON training.plan_exercises;
                DROP TRIGGER IF EXISTS trg_plan_days_immutable ON training.plan_days;
                DROP TRIGGER IF EXISTS trg_plan_versions_immutable ON training.plan_versions;
                DROP FUNCTION IF EXISTS training.forbid_history_change();
                ALTER TABLE training.plans DROP CONSTRAINT IF EXISTS fk_plans_current_version;
                ALTER TABLE training.workout_sessions DROP CONSTRAINT IF EXISTS fk_workout_sessions_recorded_by, DROP CONSTRAINT IF EXISTS fk_workout_sessions_member;
                ALTER TABLE training.training_assignments DROP CONSTRAINT IF EXISTS fk_training_assignments_assigned_by, DROP CONSTRAINT IF EXISTS fk_training_assignments_member;
                """);

            TenantTableSecurity.DisableRowLevelSecurity(migrationBuilder, TenantTables);
            migrationBuilder.Sql("ALTER TABLE training.training_assignments DROP CONSTRAINT IF EXISTS fk_training_assignments_branch;");
            foreach (var table in TenantTables)
            {
                migrationBuilder.Sql($"ALTER TABLE {table} DROP CONSTRAINT IF EXISTS fk_{table[(table.IndexOf('.') + 1)..]}_tenant;");
            }
        }
    }
}
