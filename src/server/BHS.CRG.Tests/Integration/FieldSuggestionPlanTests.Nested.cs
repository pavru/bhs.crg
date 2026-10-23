using System.Text;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Objects.FieldSuggestions;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Persistence.Configurations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// План запроса по строкам массивов (issue #1532): окно — индексом, число строк — только у записей,
/// прошедших предел объёма, значения — только у записей, прошедших ещё и предел строк.
/// </summary>
public partial class FieldSuggestionPlanTests
{
    private static readonly SuggestionPath Works = new([new PathStep("Работы", true)], "Наименование");

    [Fact]
    public async Task Строки_разбираются_только_у_записей_окна()
    {
        var owner = await JournalTypeAsync();
        var sources = new[] { new NestedSource(owner, [Works]) };

        // Объём режет окно, строки — нет: обе выборки по ключу — у одних и тех же записей, их меньше предела.
        var wide = await ExplainNestedAsync(sources, Window with { MaxLines = int.MaxValue });
        Assert.True(wide.Contains($"Index Scan using \"{DomainObjectConfiguration.FieldSuggestionsIndex}\""), wide);
        Assert.True(wide.Contains("Limit"), wide);
        Assert.False(wide.Contains("Seq Scan on domain_objects"), wide);
        var (lines, values) = Lookups(wide);
        Assert.InRange(lines, 1, Window.MaxRecords - 1);
        Assert.Equal(lines, values);

        // Предел строк — у пяти журналов по 40 строк: значения разбираются только у них.
        var narrow = await ExplainNestedAsync(sources, Window with { MaxLines = 200 });
        Assert.Equal((lines, 5), Lookups(narrow));
    }

    /// <summary>
    /// Обращения по первичному ключу — сначала счётчик строк (у окна по объёму), затем значения (у окна
    /// по строкам): узлы идут в плане в этом порядке.
    /// </summary>
    private static (int Lines, int Values) Lookups(string plan)
    {
        var loops = FieldReads().Matches(plan).Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.True(loops.Count == 2, plan);
        return (loops[0], loops[1]);
    }

    private async Task<string> ExplainNestedAsync(IReadOnlyList<NestedSource> sources, SuggestionWindow window)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using (var analyze = new NpgsqlCommand("ANALYZE domain_objects", connection))
            await analyze.ExecuteNonQueryAsync();

        await using var cmd = new NpgsqlCommand("EXPLAIN (ANALYZE, COSTS OFF) " + FieldSuggestionReader.NestedSql(sources.Count), connection);
        FieldSuggestionReader.AddNestedParameters(cmd, sources, window);
        var lines = new StringBuilder();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) lines.AppendLine(reader.GetString(0));
        return lines.ToString();
    }

    /// <summary>
    /// Тип-владелец: тысяча журналов по 40 строк с «картинкой» ~20 КБ каждый и старые лёгкие записи — как
    /// у тяжёлого типа верхнего уровня, одним запросом.
    /// </summary>
    private async Task<Guid> JournalTypeAsync()
    {
        using var scope = host.Services.CreateScope();
        var types = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
        var mark = Guid.NewGuid().ToString("N")[..8];
        var type = DocumentType.Create($"Журнал {mark}", $"Журнал{mark}", DocumentTypeKind.Composite, null,
            JsonDocument.Parse("""{"fields":[{"key":"Изображение","type":"string"}]}"""), TypeOwner.Core, TypeVisibility.Shared);
        await types.AddAsync(type);
        await types.SaveChangesAsync();

        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var insert = new NpgsqlCommand("""
                INSERT INTO domain_objects ("Id", "CompositeTypeId", "DisplayName", "Aliases", "Data", "ScopeLevel",
                                            "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), @type, 'Журнал ' || g, '{}',
                       jsonb_build_object(
                           'Изображение', (SELECT string_agg(md5(random()::text || i), '')
                                           FROM generate_series(1, 600 + g % 2) i),
                           'Работы', (SELECT jsonb_agg(jsonb_build_object('Наименование', 'Работа ' || (r % 7)))
                                      FROM generate_series(1, 40) r)),
                       'System', @at + g * interval '1 minute', @at + g * interval '1 minute'
                FROM generate_series(1, @count) AS g
                """, connection);
            insert.Parameters.AddWithValue("type", type.Id);
            insert.Parameters.AddWithValue("at", at);
            insert.Parameters.AddWithValue("count", Records);
            await insert.ExecuteNonQueryAsync();
        }
        await InsertLightAsync(type.Id, Old, at, -1, "Старый");
        return type.Id;
    }
}
