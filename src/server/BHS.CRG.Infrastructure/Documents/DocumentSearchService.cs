using BHS.CRG.Application.Documents;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Documents;

/// <summary>
/// Поиск документов по комплектам одним SQL-джойном instance→type→set→section→construction с ILIKE
/// по имени документа, имени типа и тексту реквизитов (<c>Requisites::text</c>). jsonb-текст удобнее
/// искать сырым параметризованным SQL, чем LINQ-выражением. Индексы пока не нужны (десятки тысяч строк).
/// </summary>
public class DocumentSearchService(AppDbContext db) : IDocumentSearch
{
    private const int MaxResults = 200;

    public async Task<IReadOnlyList<DocumentSearchResult>> SearchAsync(
        string text, Guid? constructionId, CancellationToken ct = default)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return [];

        var pattern = $"%{EscapeLike(trimmed)}%";
        var cid = constructionId;

        // Документы — DomainObject с документной фасетой (issue #84): INNER JOIN document_facets
        // отбирает только документы; их расположение — (Set, ScopeId=setId).
        FormattableString sql = $"""
            SELECT o."Id"            AS "InstanceId",
                   o."DisplayName"   AS "Name",
                   dt."Name"         AS "TypeName",
                   f."Status"        AS "Status",
                   EXISTS (SELECT 1 FROM generated_files gf
                           WHERE gf."ObjectId" = o."Id" AND gf."Format" = 'Pdf') AS "HasPdf",
                   c."Id"            AS "ConstructionId",
                   c."Name"          AS "ConstructionName",
                   sec."Name"        AS "SectionName",
                   ds."Id"           AS "SetId",
                   ds."Name"         AS "SetName"
            FROM domain_objects o
            JOIN document_facets f  ON f."ObjectId" = o."Id"
            JOIN document_types dt  ON dt."Id"  = o."CompositeTypeId"
            JOIN document_sets ds   ON ds."Id"  = o."ScopeId"
            JOIN sections sec       ON sec."Id" = ds."SectionId"
            JOIN constructions c    ON c."Id"   = sec."ConstructionId"
            WHERE ({cid}::uuid IS NULL OR c."Id" = {cid}::uuid)
              AND (   o."DisplayName" ILIKE {pattern} ESCAPE '\'
                   OR dt."Name" ILIKE {pattern} ESCAPE '\'
                   OR o."Data"::text ILIKE {pattern} ESCAPE '\' )
            ORDER BY c."Name", sec."Name", ds."Name", f."SortOrder"
            LIMIT {MaxResults}
            """;

        return await db.Database.SqlQuery<DocumentSearchResult>(sql).ToListAsync(ct);
    }

    // Экранируем спецсимволы ILIKE, чтобы ввод пользователя искался буквально (contains).
    private static string EscapeLike(string s)
        => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
