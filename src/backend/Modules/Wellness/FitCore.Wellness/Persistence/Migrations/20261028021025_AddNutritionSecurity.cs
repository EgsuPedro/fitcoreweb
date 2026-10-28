using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Wellness.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNutritionSecurity : Migration
    {
        private static readonly string[] TenantTables =
        [
            "nutrition.plans",
            "nutrition.plan_versions",
            "nutrition.meals",
            "nutrition.meal_items",
            "nutrition.nutrition_assignments",
            "nutrition.dietary_profiles",
            "nutrition.meal_logs"
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantTables)
            {
                TenantTableSecurity.AddTenantForeignKey(migrationBuilder, table, $"fk_nutrition_{table[(table.IndexOf('.') + 1)..]}_tenant");
            }

            TenantTableSecurity.AddBranchForeignKey(migrationBuilder, "nutrition.nutrition_assignments", "fk_nutrition_assignments_branch");
            TenantTableSecurity.EnableRowLevelSecurity(migrationBuilder, TenantTables);
            TenantTableSecurity.GrantApplicationRole(migrationBuilder, "nutrition");

            migrationBuilder.Sql("""
                -- Los alumnos y quienes asignan o registran pertenecen al mismo gimnasio.
                ALTER TABLE nutrition.nutrition_assignments ADD CONSTRAINT fk_nutrition_assignments_member
                  FOREIGN KEY (tenant_id, member_id) REFERENCES members.members (tenant_id, id);
                ALTER TABLE nutrition.nutrition_assignments ADD CONSTRAINT fk_nutrition_assignments_assigned_by
                  FOREIGN KEY (assigned_by) REFERENCES identity.app_users (id);
                ALTER TABLE nutrition.dietary_profiles ADD CONSTRAINT fk_dietary_profiles_member
                  FOREIGN KEY (tenant_id, member_id) REFERENCES members.members (tenant_id, id);
                ALTER TABLE nutrition.dietary_profiles ADD CONSTRAINT fk_dietary_profiles_recorded_by
                  FOREIGN KEY (recorded_by) REFERENCES identity.app_users (id);
                ALTER TABLE nutrition.meal_logs ADD CONSTRAINT fk_meal_logs_member
                  FOREIGN KEY (tenant_id, member_id) REFERENCES members.members (tenant_id, id);
                ALTER TABLE nutrition.meal_logs ADD CONSTRAINT fk_meal_logs_recorded_by
                  FOREIGN KEY (recorded_by) REFERENCES identity.app_users (id);

                -- La cabecera apunta a su versión vigente; ambas se crean en la misma transacción, por eso la referencia se comprueba al final.
                ALTER TABLE nutrition.plans ADD CONSTRAINT fk_nutrition_plans_current_version
                  FOREIGN KEY (current_version_id) REFERENCES nutrition.plan_versions (id) DEFERRABLE INITIALLY DEFERRED;

                ALTER TABLE nutrition.plans
                  ADD CONSTRAINT ck_nutrition_plans_status CHECK (status IN ('Active', 'Archived')),
                  ADD CONSTRAINT ck_nutrition_plans_version CHECK (current_version >= 0);

                -- Objetivos orientativos: solo se exige que sean valores razonables.
                ALTER TABLE nutrition.plan_versions
                  ADD CONSTRAINT ck_nutrition_versions_validity CHECK (valid_from IS NULL OR valid_until IS NULL OR valid_until >= valid_from),
                  ADD CONSTRAINT ck_nutrition_versions_calories CHECK (target_calories IS NULL OR target_calories BETWEEN 300 AND 10000),
                  ADD CONSTRAINT ck_nutrition_versions_protein CHECK (target_protein_g IS NULL OR target_protein_g BETWEEN 0 AND 1000),
                  ADD CONSTRAINT ck_nutrition_versions_carbs CHECK (target_carbs_g IS NULL OR target_carbs_g BETWEEN 0 AND 1000),
                  ADD CONSTRAINT ck_nutrition_versions_fat CHECK (target_fat_g IS NULL OR target_fat_g BETWEEN 0 AND 1000);

                ALTER TABLE nutrition.meal_items
                  ADD CONSTRAINT ck_meal_items_quantity CHECK (quantity > 0 AND quantity <= 100000),
                  ADD CONSTRAINT ck_meal_items_unit CHECK (length(btrim(unit)) > 0),
                  ADD CONSTRAINT ck_meal_items_calories CHECK (calories IS NULL OR calories BETWEEN 0 AND 5000),
                  ADD CONSTRAINT ck_meal_items_protein CHECK (protein_g IS NULL OR protein_g BETWEEN 0 AND 1000),
                  ADD CONSTRAINT ck_meal_items_carbs CHECK (carbs_g IS NULL OR carbs_g BETWEEN 0 AND 1000),
                  ADD CONSTRAINT ck_meal_items_fat CHECK (fat_g IS NULL OR fat_g BETWEEN 0 AND 1000);

                ALTER TABLE nutrition.nutrition_assignments
                  ADD CONSTRAINT ck_nutrition_assignments_range CHECK (end_date IS NULL OR end_date >= start_date),
                  ADD CONSTRAINT ck_nutrition_assignments_ended CHECK (
                    (ended_on IS NULL) = (end_reason IS NULL) AND (ended_on IS NULL OR ended_on >= start_date - 1));

                -- Alergias y restricciones: listas acotadas y nunca un registro completamente vacío.
                ALTER TABLE nutrition.dietary_profiles
                  ADD CONSTRAINT ck_dietary_profiles_sizes CHECK (
                    cardinality(allergies) <= 20 AND cardinality(intolerances) <= 20 AND cardinality(restrictions) <= 20),
                  ADD CONSTRAINT ck_dietary_profiles_content CHECK (
                    cardinality(allergies) + cardinality(intolerances) + cardinality(restrictions) > 0 OR notes IS NOT NULL);

                ALTER TABLE nutrition.meal_logs
                  ADD CONSTRAINT ck_meal_logs_status CHECK (status IN ('Completed', 'Partial', 'Skipped'));

                -- Historial inmutable: versiones, comidas, alimentos, perfiles alimentarios y registros de cumplimiento nunca se reescriben ni se borran.
                CREATE FUNCTION nutrition.forbid_history_change() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  RAISE EXCEPTION 'El historial de nutrición no se puede modificar ni borrar' USING ERRCODE = '23514';
                END
                $fn$;
                CREATE TRIGGER trg_nutrition_versions_immutable BEFORE UPDATE OR DELETE ON nutrition.plan_versions
                  FOR EACH ROW EXECUTE FUNCTION nutrition.forbid_history_change();
                CREATE TRIGGER trg_nutrition_meals_immutable BEFORE UPDATE OR DELETE ON nutrition.meals
                  FOR EACH ROW EXECUTE FUNCTION nutrition.forbid_history_change();
                CREATE TRIGGER trg_nutrition_meal_items_immutable BEFORE UPDATE OR DELETE ON nutrition.meal_items
                  FOR EACH ROW EXECUTE FUNCTION nutrition.forbid_history_change();
                CREATE TRIGGER trg_dietary_profiles_immutable BEFORE UPDATE OR DELETE ON nutrition.dietary_profiles
                  FOR EACH ROW EXECUTE FUNCTION nutrition.forbid_history_change();
                CREATE TRIGGER trg_meal_logs_immutable BEFORE UPDATE OR DELETE ON nutrition.meal_logs
                  FOR EACH ROW EXECUTE FUNCTION nutrition.forbid_history_change();
                CREATE TRIGGER trg_nutrition_plans_no_delete BEFORE DELETE ON nutrition.plans
                  FOR EACH ROW EXECUTE FUNCTION nutrition.forbid_history_change();
                CREATE TRIGGER trg_nutrition_assignments_no_delete BEFORE DELETE ON nutrition.nutrition_assignments
                  FOR EACH ROW EXECUTE FUNCTION nutrition.forbid_history_change();

                -- La versión vigente de un plan solo avanza, y la cabecera nunca cambia de gimnasio.
                CREATE FUNCTION nutrition.guard_plan_update() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
                    RAISE EXCEPTION 'Los datos de origen del plan no se pueden modificar' USING ERRCODE = '23514';
                  END IF;
                  IF NEW.current_version < OLD.current_version THEN
                    RAISE EXCEPTION 'La versión de un plan no puede retroceder' USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_nutrition_plans_guard_update BEFORE UPDATE ON nutrition.plans
                  FOR EACH ROW EXECUTE FUNCTION nutrition.guard_plan_update();

                -- Una asignación solo cambia al cerrarse; cerrada es definitiva y sus datos nunca se reescriben.
                CREATE FUNCTION nutrition.guard_assignment_update() RETURNS trigger LANGUAGE plpgsql AS $fn$
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
                CREATE TRIGGER trg_nutrition_assignments_guard_update BEFORE UPDATE ON nutrition.nutrition_assignments
                  FOR EACH ROW EXECUTE FUNCTION nutrition.guard_assignment_update();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_nutrition_assignments_guard_update ON nutrition.nutrition_assignments;
                DROP FUNCTION IF EXISTS nutrition.guard_assignment_update();
                DROP TRIGGER IF EXISTS trg_nutrition_plans_guard_update ON nutrition.plans;
                DROP FUNCTION IF EXISTS nutrition.guard_plan_update();
                DROP TRIGGER IF EXISTS trg_nutrition_assignments_no_delete ON nutrition.nutrition_assignments;
                DROP TRIGGER IF EXISTS trg_nutrition_plans_no_delete ON nutrition.plans;
                DROP TRIGGER IF EXISTS trg_meal_logs_immutable ON nutrition.meal_logs;
                DROP TRIGGER IF EXISTS trg_dietary_profiles_immutable ON nutrition.dietary_profiles;
                DROP TRIGGER IF EXISTS trg_nutrition_meal_items_immutable ON nutrition.meal_items;
                DROP TRIGGER IF EXISTS trg_nutrition_meals_immutable ON nutrition.meals;
                DROP TRIGGER IF EXISTS trg_nutrition_versions_immutable ON nutrition.plan_versions;
                DROP FUNCTION IF EXISTS nutrition.forbid_history_change();
                ALTER TABLE nutrition.plans DROP CONSTRAINT IF EXISTS fk_nutrition_plans_current_version;
                ALTER TABLE nutrition.meal_logs DROP CONSTRAINT IF EXISTS fk_meal_logs_recorded_by, DROP CONSTRAINT IF EXISTS fk_meal_logs_member;
                ALTER TABLE nutrition.dietary_profiles DROP CONSTRAINT IF EXISTS fk_dietary_profiles_recorded_by, DROP CONSTRAINT IF EXISTS fk_dietary_profiles_member;
                ALTER TABLE nutrition.nutrition_assignments DROP CONSTRAINT IF EXISTS fk_nutrition_assignments_assigned_by, DROP CONSTRAINT IF EXISTS fk_nutrition_assignments_member;
                """);

            TenantTableSecurity.DisableRowLevelSecurity(migrationBuilder, TenantTables);
            migrationBuilder.Sql("ALTER TABLE nutrition.nutrition_assignments DROP CONSTRAINT IF EXISTS fk_nutrition_assignments_branch;");
            foreach (var table in TenantTables)
            {
                migrationBuilder.Sql($"ALTER TABLE {table} DROP CONSTRAINT IF EXISTS fk_nutrition_{table[(table.IndexOf('.') + 1)..]}_tenant;");
            }
        }
    }
}
