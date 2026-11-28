using Microsoft.EntityFrameworkCore.Migrations;

namespace BHS.CRG.Modules.Data;

/// <summary>Ревизии для полевого снимка. Каждый модуль мигрирует и пишет только свою схему.</summary>
public static class FieldReferenceRevisionMigration
{
    public const string TableName = "field_reference_revisions";

    public static void Add(MigrationBuilder migration, string schema, params string[] tables)
    {
        migration.Sql($"""
            CREATE TABLE {schema}.{TableName} (
                writer integer PRIMARY KEY,
                revision text NOT NULL
            );
            CREATE FUNCTION {schema}.mark_field_reference_changed() RETURNS trigger
            LANGUAGE plpgsql AS $$
            DECLARE writer_slot integer;
            BEGIN
                SELECT backend INTO STRICT writer_slot FROM pg_stat_get_backend_idset() AS backend
                WHERE pg_stat_get_backend_pid(backend) = pg_backend_pid();

                INSERT INTO {schema}.{TableName} AS tracked (writer, revision)
                VALUES (writer_slot, pg_current_xact_id()::text)
                ON CONFLICT (writer) DO UPDATE SET revision = EXCLUDED.revision
                WHERE tracked.revision <> EXCLUDED.revision;
                RETURN NULL;
            END $$;
            """);
        foreach (var table in tables)
            migration.Sql($"""
                CREATE TRIGGER field_reference_changed AFTER INSERT OR UPDATE OR DELETE OR TRUNCATE ON {schema}.{table}
                    FOR EACH STATEMENT EXECUTE FUNCTION {schema}.mark_field_reference_changed();
                """);
    }

    public static void Remove(MigrationBuilder migration, string schema, params string[] tables)
    {
        foreach (var table in tables)
            migration.Sql($"DROP TRIGGER field_reference_changed ON {schema}.{table};");
        migration.Sql($"""
            DROP FUNCTION {schema}.mark_field_reference_changed();
            DROP TABLE {schema}.{TableName};
            """);
    }
}
