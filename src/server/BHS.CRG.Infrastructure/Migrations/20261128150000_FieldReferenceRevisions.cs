using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BHS.CRG.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261128150000_FieldReferenceRevisions")]
public sealed class FieldReferenceRevisions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE public.field_reference_revisions (
            source text NOT NULL,
            writer integer NOT NULL,
            revision text NOT NULL,
            PRIMARY KEY (source, writer)
        );

        CREATE FUNCTION public.mark_field_reference_changed() RETURNS trigger
        LANGUAGE plpgsql AS $$
        DECLARE writer_slot integer;
        BEGIN
            -- Одно место на источник и слот соединения. Параллельные писатели не блокируют друг друга.
            SELECT backend INTO STRICT writer_slot
            FROM pg_stat_get_backend_idset() AS backend
            WHERE pg_stat_get_backend_pid(backend) = pg_backend_pid();

            INSERT INTO public.field_reference_revisions AS tracked (source, writer, revision)
            VALUES (TG_ARGV[0], writer_slot, pg_current_xact_id()::text)
            ON CONFLICT (source, writer) DO UPDATE SET revision = EXCLUDED.revision
            WHERE tracked.revision <> EXCLUDED.revision;
            RETURN NULL;
        END $$;

        CREATE TRIGGER field_reference_changed AFTER INSERT OR UPDATE OR DELETE OR TRUNCATE ON public.domain_objects
            FOR EACH STATEMENT EXECUTE FUNCTION public.mark_field_reference_changed('records');
        CREATE TRIGGER field_reference_changed AFTER INSERT OR UPDATE OR DELETE OR TRUNCATE ON public.document_types
            FOR EACH STATEMENT EXECUTE FUNCTION public.mark_field_reference_changed('records');
        CREATE TRIGGER field_reference_changed AFTER INSERT OR UPDATE OR DELETE OR TRUNCATE ON public.constructions
            FOR EACH STATEMENT EXECUTE FUNCTION public.mark_field_reference_changed('sites');
        CREATE TRIGGER field_reference_changed AFTER INSERT OR UPDATE OR DELETE OR TRUNCATE ON public.sections
            FOR EACH STATEMENT EXECUTE FUNCTION public.mark_field_reference_changed('sections');
        CREATE TRIGGER field_reference_changed AFTER INSERT OR UPDATE OR DELETE OR TRUNCATE ON public.document_sets
            FOR EACH STATEMENT EXECUTE FUNCTION public.mark_field_reference_changed('sections');
        CREATE TRIGGER field_reference_changed AFTER INSERT OR UPDATE OR DELETE OR TRUNCATE ON public.app_settings
            FOR EACH STATEMENT EXECUTE FUNCTION public.mark_field_reference_changed('company');
        CREATE TRIGGER field_reference_changed AFTER INSERT OR UPDATE OR DELETE OR TRUNCATE ON public.work_plan_items
            FOR EACH STATEMENT EXECUTE FUNCTION public.mark_field_reference_changed('positions');
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP TRIGGER field_reference_changed ON public.domain_objects;
        DROP TRIGGER field_reference_changed ON public.document_types;
        DROP TRIGGER field_reference_changed ON public.constructions;
        DROP TRIGGER field_reference_changed ON public.sections;
        DROP TRIGGER field_reference_changed ON public.document_sets;
        DROP TRIGGER field_reference_changed ON public.app_settings;
        DROP TRIGGER field_reference_changed ON public.work_plan_items;
        DROP FUNCTION public.mark_field_reference_changed();
        DROP TABLE public.field_reference_revisions;
        """);
}
