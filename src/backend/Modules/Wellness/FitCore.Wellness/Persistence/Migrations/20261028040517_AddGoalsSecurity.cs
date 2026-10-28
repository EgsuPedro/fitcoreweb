using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Wellness.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGoalsSecurity : Migration
    {
        private static readonly string[] TenantTables = ["wellness.goals"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            TenantTableSecurity.AddTenantForeignKey(migrationBuilder, "wellness.goals", "fk_goals_tenant");
            TenantTableSecurity.EnableRowLevelSecurity(migrationBuilder, TenantTables);
            TenantTableSecurity.GrantApplicationRole(migrationBuilder, "wellness");

            migrationBuilder.Sql("""
                ALTER TABLE wellness.goals ADD CONSTRAINT fk_goals_member
                  FOREIGN KEY (tenant_id, member_id) REFERENCES members.members (tenant_id, id);
                ALTER TABLE wellness.goals ADD CONSTRAINT fk_goals_created_by
                  FOREIGN KEY (created_by) REFERENCES identity.app_users (id);

                ALTER TABLE wellness.goals
                  ADD CONSTRAINT ck_goals_category CHECK (category IN ('BodyMetric', 'SessionsPerWeek', 'Custom')),
                  ADD CONSTRAINT ck_goals_metric CHECK (metric IS NULL OR metric IN
                    ('WeightKg', 'WaistCm', 'HipCm', 'ChestCm', 'ArmCm', 'ThighCm', 'BodyFatPercent', 'SessionsPerWeek')),
                  ADD CONSTRAINT ck_goals_status CHECK (status IN ('Active', 'Achieved', 'Abandoned')),
                  -- Cada categoría exige su medida: las libres no tienen medida ni meta; las de constancia, solo sesiones por semana.
                  ADD CONSTRAINT ck_goals_category_metric CHECK (
                    (category = 'Custom' AND metric IS NULL AND target_value IS NULL)
                    OR (category = 'SessionsPerWeek' AND metric = 'SessionsPerWeek' AND target_value BETWEEN 1 AND 14)
                    OR (category = 'BodyMetric' AND metric IS NOT NULL AND metric <> 'SessionsPerWeek' AND target_value IS NOT NULL AND target_value > 0)),
                  ADD CONSTRAINT ck_goals_dates CHECK (target_date IS NULL OR target_date > start_date),
                  -- Abierto = sin fecha de cierre; cerrado = con ella, y abandonar exige motivo.
                  ADD CONSTRAINT ck_goals_closed CHECK (
                    (status = 'Active' AND closed_on IS NULL)
                    OR (status = 'Achieved' AND closed_on IS NOT NULL)
                    OR (status = 'Abandoned' AND closed_on IS NOT NULL AND close_note IS NOT NULL));

                -- Un objetivo nunca se borra y, una vez creado, solo cambia al cerrarse; cerrado es definitivo.
                CREATE FUNCTION wellness.guard_goal_update() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  IF TG_OP = 'DELETE' THEN
                    RAISE EXCEPTION 'Los objetivos no se pueden borrar' USING ERRCODE = '23514';
                  END IF;
                  IF OLD.status <> 'Active' THEN
                    RAISE EXCEPTION 'Un objetivo cerrado no se puede modificar' USING ERRCODE = '23514';
                  END IF;
                  IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id OR NEW.member_id IS DISTINCT FROM OLD.member_id
                     OR NEW.title IS DISTINCT FROM OLD.title OR NEW.description IS DISTINCT FROM OLD.description
                     OR NEW.category IS DISTINCT FROM OLD.category OR NEW.metric IS DISTINCT FROM OLD.metric
                     OR NEW.target_value IS DISTINCT FROM OLD.target_value OR NEW.baseline_value IS DISTINCT FROM OLD.baseline_value
                     OR NEW.start_date IS DISTINCT FROM OLD.start_date OR NEW.target_date IS DISTINCT FROM OLD.target_date
                     OR NEW.created_by IS DISTINCT FROM OLD.created_by OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
                    RAISE EXCEPTION 'La definición del objetivo no se puede modificar: ciérrelo y cree otro' USING ERRCODE = '23514';
                  END IF;
                  RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_goals_guard BEFORE UPDATE OR DELETE ON wellness.goals
                  FOR EACH ROW EXECUTE FUNCTION wellness.guard_goal_update();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_goals_guard ON wellness.goals;
                DROP FUNCTION IF EXISTS wellness.guard_goal_update();
                ALTER TABLE wellness.goals DROP CONSTRAINT IF EXISTS fk_goals_created_by, DROP CONSTRAINT IF EXISTS fk_goals_member;
                """);
            TenantTableSecurity.DisableRowLevelSecurity(migrationBuilder, TenantTables);
            migrationBuilder.Sql("ALTER TABLE wellness.goals DROP CONSTRAINT IF EXISTS fk_goals_tenant;");
        }
    }
}
