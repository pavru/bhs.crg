using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Api.Modules.Ports;

internal sealed class FieldReferenceRevisionsPort(AppDbContext db) : IFieldReferenceRevisions
{
    public Task<string> ReadAsync(string kind, CancellationToken ct)
    {
        string[] sources = kind switch
        {
            "sites" => ["sites", "company", "records", "sections"],
            "sections" => ["sites", "sections"],
            "positions" => ["sites", "positions"],
            "participants" or "issued-materials" or "estimated-materials" => ["sites", "records"],
            _ => ["records"],
        };
        // Часть транзакций фиксируется не по порядку xid, поэтому сравниваем весь компактный вектор.
        return db.Database.SqlQuery<string>($"""
            SELECT COALESCE(string_agg(source || ':' || writer || ':' || revision, ',' ORDER BY source, writer), '') AS "Value"
            FROM public.field_reference_revisions
            WHERE source = ANY({sources})
            """).SingleAsync(ct);
    }
}
