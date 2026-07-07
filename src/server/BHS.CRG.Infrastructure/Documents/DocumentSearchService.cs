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

        FormattableString sql = $"""
            SELECT i."Id"            AS "InstanceId",
                   i."Name"          AS "Name",
                   dt."Name"         AS "TypeName",
                   i."Status"        AS "Status",
                   EXISTS (SELECT 1 FROM generated_files gf
                           WHERE gf."DocumentInstanceId" = i."Id" AND gf."Format" = 'Pdf') AS "HasPdf",
                   c."Id"            AS "ConstructionId",
                   c."Name"          AS "ConstructionName",
                   sec."Name"        AS "SectionName",
                   ds."Id"           AS "SetId",
                   ds."Name"         AS "SetName"
            FROM document_instances i
            JOIN document_types dt  ON dt."Id"  = i."DocumentTypeId"
            JOIN document_sets ds   ON ds."Id"  = i."DocumentSetId"
            JOIN sections sec       ON sec."Id" = ds."SectionId"
            JOIN constructions c    ON c."Id"   = sec."ConstructionId"
            WHERE ({cid}::uuid IS NULL OR c."Id" = {cid}::uuid)
              AND (   i."Name" ILIKE {pattern} ESCAPE '\'
                   OR dt."Name" ILIKE {pattern} ESCAPE '\'
                   OR i."Requisites"::text ILIKE {pattern} ESCAPE '\' )
            ORDER BY c."Name", sec."Name", ds."Name", i."SortOrder"
            LIMIT {MaxResults}
            """;

        return await db.Database.SqlQuery<DocumentSearchResult>(sql).ToListAsync(ct);
    }

    // Экранируем спецсимволы ILIKE, чтобы ввод пользователя искался буквально (contains).
    private static string EscapeLike(string s)
        => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
