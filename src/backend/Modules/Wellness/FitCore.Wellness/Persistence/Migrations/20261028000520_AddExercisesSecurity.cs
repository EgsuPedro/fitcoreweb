using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FitCore.Wellness.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExercisesSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            TenantTableSecurity.GrantApplicationRole(migrationBuilder, "training");

            migrationBuilder.Sql("""
                -- Los ejercicios globales (tenant_id nulo) los ve cualquier gimnasio, pero solo la plataforma los crea y los edita.
                -- Cada operación tiene su política: así un gimnasio no puede reescribir ni borrar el catálogo base ni con un descuido
                -- de la aplicación.
                ALTER TABLE training.exercises ENABLE ROW LEVEL SECURITY;
                ALTER TABLE training.exercises FORCE ROW LEVEL SECURITY;

                CREATE POLICY exercises_select ON training.exercises FOR SELECT
                  USING (tenant_id IS NULL OR tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

                CREATE POLICY exercises_insert ON training.exercises FOR INSERT
                  WITH CHECK (
                    tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                    OR (tenant_id IS NULL AND EXISTS (
                          SELECT 1 FROM organizations.tenants t
                          WHERE t.id = NULLIF(current_setting('app.tenant_id', true), '')::uuid AND t.slug = 'platform')));

                CREATE POLICY exercises_update ON training.exercises FOR UPDATE
                  USING (
                    tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                    OR (tenant_id IS NULL AND EXISTS (
                          SELECT 1 FROM organizations.tenants t
                          WHERE t.id = NULLIF(current_setting('app.tenant_id', true), '')::uuid AND t.slug = 'platform')))
                  WITH CHECK (
                    tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                    OR (tenant_id IS NULL AND EXISTS (
                          SELECT 1 FROM organizations.tenants t
                          WHERE t.id = NULLIF(current_setting('app.tenant_id', true), '')::uuid AND t.slug = 'platform')));

                -- Los ejercicios nunca se borran (las rutinas históricas los referencian): ninguna política de DELETE.

                ALTER TABLE training.exercises ADD CONSTRAINT fk_exercises_tenant FOREIGN KEY (tenant_id) REFERENCES organizations.tenants (id);

                ALTER TABLE training.exercises
                  ADD CONSTRAINT ck_exercises_primary CHECK (primary_muscle IN ('Chest','Back','Shoulders','Biceps','Triceps','Forearms','Core','Quadriceps','Hamstrings','Glutes','Calves','FullBody')),
                  ADD CONSTRAINT ck_exercises_type CHECK (type IN ('Strength','Cardio','Mobility','Flexibility','Balance','Plyometric')),
                  ADD CONSTRAINT ck_exercises_equipment CHECK (equipment IN ('BodyWeight','Barbell','Dumbbell','Kettlebell','Machine','Cable','Band','Bench','PullUpBar','CardioMachine','Other')),
                  ADD CONSTRAINT ck_exercises_difficulty CHECK (difficulty IN ('Beginner','Intermediate','Advanced')),
                  ADD CONSTRAINT ck_exercises_tracking CHECK (tracking_mode IN ('RepsAndLoad','Reps','Duration')),
                  ADD CONSTRAINT ck_exercises_secondary CHECK (cardinality(secondary_muscles) <= 5 AND NOT (primary_muscle = ANY (secondary_muscles))),
                  ADD CONSTRAINT ck_exercises_video CHECK (video_url IS NULL OR video_url ~ '^https://[^ ]+$');

                -- Nombres únicos sin distinguir mayúsculas: entre los globales y dentro de cada gimnasio.
                DROP INDEX IF EXISTS training.ux_exercises_global_name;
                DROP INDEX IF EXISTS training.ux_exercises_tenant_name;
                CREATE UNIQUE INDEX ux_exercises_global_name ON training.exercises (lower(name)) WHERE tenant_id IS NULL;
                CREATE UNIQUE INDEX ux_exercises_tenant_name ON training.exercises (tenant_id, lower(name)) WHERE tenant_id IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS training.ux_exercises_global_name;
                DROP INDEX IF EXISTS training.ux_exercises_tenant_name;
                ALTER TABLE training.exercises
                  DROP CONSTRAINT IF EXISTS ck_exercises_primary, DROP CONSTRAINT IF EXISTS ck_exercises_type,
                  DROP CONSTRAINT IF EXISTS ck_exercises_equipment, DROP CONSTRAINT IF EXISTS ck_exercises_difficulty,
                  DROP CONSTRAINT IF EXISTS ck_exercises_tracking, DROP CONSTRAINT IF EXISTS ck_exercises_secondary,
                  DROP CONSTRAINT IF EXISTS ck_exercises_video, DROP CONSTRAINT IF EXISTS fk_exercises_tenant;
                DROP POLICY IF EXISTS exercises_update ON training.exercises;
                DROP POLICY IF EXISTS exercises_insert ON training.exercises;
                DROP POLICY IF EXISTS exercises_select ON training.exercises;
                ALTER TABLE training.exercises NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE training.exercises DISABLE ROW LEVEL SECURITY;
                """);
        }
    }
}
