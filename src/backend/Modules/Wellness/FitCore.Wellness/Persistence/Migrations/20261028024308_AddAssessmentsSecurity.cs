using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Wellness.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAssessmentsSecurity : Migration
    {
        private static readonly string[] TenantTables = ["wellness.body_assessments"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            TenantTableSecurity.AddTenantForeignKey(migrationBuilder, "wellness.body_assessments", "fk_body_assessments_tenant");
            TenantTableSecurity.EnableRowLevelSecurity(migrationBuilder, TenantTables);
            TenantTableSecurity.GrantApplicationRole(migrationBuilder, "wellness");

            migrationBuilder.Sql("""
                ALTER TABLE wellness.body_assessments ADD CONSTRAINT fk_body_assessments_member
                  FOREIGN KEY (tenant_id, member_id) REFERENCES members.members (tenant_id, id);
                ALTER TABLE wellness.body_assessments ADD CONSTRAINT fk_body_assessments_recorded_by
                  FOREIGN KEY (recorded_by) REFERENCES identity.app_users (id);

                -- Las mediciones deben ser razonables y, si se registra grasa corporal, con el método con que se midió.
                ALTER TABLE wellness.body_assessments
                  ADD CONSTRAINT ck_body_assessments_weight CHECK (weight_kg IS NULL OR weight_kg BETWEEN 2 AND 500),
                  ADD CONSTRAINT ck_body_assessments_height CHECK (height_cm IS NULL OR height_cm BETWEEN 50 AND 260),
                  ADD CONSTRAINT ck_body_assessments_waist CHECK (waist_cm IS NULL OR waist_cm BETWEEN 5 AND 250),
                  ADD CONSTRAINT ck_body_assessments_hip CHECK (hip_cm IS NULL OR hip_cm BETWEEN 5 AND 250),
                  ADD CONSTRAINT ck_body_assessments_chest CHECK (chest_cm IS NULL OR chest_cm BETWEEN 5 AND 250),
                  ADD CONSTRAINT ck_body_assessments_arm CHECK (arm_cm IS NULL OR arm_cm BETWEEN 5 AND 250),
                  ADD CONSTRAINT ck_body_assessments_thigh CHECK (thigh_cm IS NULL OR thigh_cm BETWEEN 5 AND 250),
                  ADD CONSTRAINT ck_body_assessments_body_fat CHECK (body_fat_percent IS NULL OR body_fat_percent BETWEEN 2 AND 70),
                  ADD CONSTRAINT ck_body_assessments_body_fat_method CHECK ((body_fat_percent IS NULL) = (body_fat_method IS NULL)),
                  ADD CONSTRAINT ck_body_assessments_source CHECK (source IN ('Manual', 'Scale', 'Tape', 'Caliper', 'Bioimpedance', 'Other')),
                  ADD CONSTRAINT ck_body_assessments_not_empty CHECK (
                    weight_kg IS NOT NULL OR height_cm IS NOT NULL OR waist_cm IS NOT NULL OR hip_cm IS NOT NULL OR chest_cm IS NOT NULL
                    OR arm_cm IS NOT NULL OR thigh_cm IS NOT NULL OR body_fat_percent IS NOT NULL);

                -- Solo se agrega: una evaluación nunca se reescribe ni se borra (los errores se corrigen con una evaluación nueva).
                CREATE FUNCTION wellness.forbid_history_change() RETURNS trigger LANGUAGE plpgsql AS $fn$
                BEGIN
                  RAISE EXCEPTION 'Las evaluaciones físicas no se pueden modificar ni borrar' USING ERRCODE = '23514';
                END
                $fn$;
                CREATE TRIGGER trg_body_assessments_immutable BEFORE UPDATE OR DELETE ON wellness.body_assessments
                  FOR EACH ROW EXECUTE FUNCTION wellness.forbid_history_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_body_assessments_immutable ON wellness.body_assessments;
                DROP FUNCTION IF EXISTS wellness.forbid_history_change();
                ALTER TABLE wellness.body_assessments DROP CONSTRAINT IF EXISTS fk_body_assessments_recorded_by, DROP CONSTRAINT IF EXISTS fk_body_assessments_member;
                """);
            TenantTableSecurity.DisableRowLevelSecurity(migrationBuilder, TenantTables);
            migrationBuilder.Sql("ALTER TABLE wellness.body_assessments DROP CONSTRAINT IF EXISTS fk_body_assessments_tenant;");
        }
    }
}
