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
        var nodes = Nodes(wide.RootElement.GetProperty("Plan"), underSubPlan: false).ToList();
        Assert.True(nodes.Any(n => n.Node.TryGetProperty("Index Name", out var i)
            && i.GetString() == DomainObjectConfiguration.FieldSuggestionsIndex), wide.RootElement.ToString());
        Assert.True(nodes.Any(n => Type(n.Node) == "Limit"), wide.RootElement.ToString());
        Assert.DoesNotContain(nodes, n => Type(n.Node) == "Seq Scan"
            && n.Node.GetProperty("Relation Name").GetString() == "domain_objects");
        var (lines, values) = Lookups(wide);
        Assert.InRange(lines, 1, Window.MaxRecords - 1);
        Assert.Equal(lines, values);

        // Предел строк — у пяти журналов по 40 строк: значения разбираются только у них.
        var narrow = await ExplainNestedAsync(sources, Window with { MaxLines = 200 });
        Assert.Equal((lines, 5), Lookups(narrow));
    }

    /// <summary>
    /// Обращения по первичному ключу: счётчик строк — подзапрос в списке выборки (узел под
    /// <c>SubPlan</c>), значения — боковое соединение. Различаются местом в дереве плана, а не порядком
    /// строк в выводе: порядок волен смениться с версией базы, и числа молча поменялись бы местами.
    /// </summary>
    private static (int Lines, int Values) Lookups(JsonDocument plan)
    {
        var lookups = Nodes(plan.RootElement.GetProperty("Plan"), underSubPlan: false)
            .Where(n => Type(n.Node) == "Index Scan" && n.Node.GetProperty("Index Name").GetString() == "PK_domain_objects")
            .ToList();
        Assert.True(lookups.Count == 2, plan.RootElement.ToString());
        var lines = Assert.Single(lookups, n => n.UnderSubPlan);
        var values = Assert.Single(lookups, n => !n.UnderSubPlan);
        return (Loops(lines.Node), Loops(values.Node));
    }

    private static string? Type(JsonElement node) => node.GetProperty("Node Type").GetString();

    private static int Loops(JsonElement node) => node.GetProperty("Actual Loops").GetInt32();

    /// <summary>Узлы плана и признак «под подзапросом списка выборки» — у узла или любого предка.</summary>
    private static IEnumerable<(JsonElement Node, bool UnderSubPlan)> Nodes(JsonElement node, bool underSubPlan)
    {
        var here = underSubPlan || (node.TryGetProperty("Parent Relationship", out var r) && r.GetString() == "SubPlan");
        yield return (node, here);
        if (!node.TryGetProperty("Plans", out var children)) yield break;
        foreach (var child in children.EnumerateArray())
            foreach (var n in Nodes(child, here)) yield return n;
    }

    private async Task<JsonDocument> ExplainNestedAsync(IReadOnlyList<NestedSource> sources, SuggestionWindow window)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using (var analyze = new NpgsqlCommand("ANALYZE domain_objects", connection))
            await analyze.ExecuteNonQueryAsync();

        await using var cmd = new NpgsqlCommand(
            "EXPLAIN (ANALYZE, COSTS OFF, FORMAT JSON) " + FieldSuggestionReader.NestedSql(sources.Count), connection);
        FieldSuggestionReader.AddNestedParameters(cmd, sources);
        FieldSuggestionReader.AddWindow(cmd, window);
        var json = (string)(await cmd.ExecuteScalarAsync())!;
        return JsonDocument.Parse(JsonDocument.Parse(json).RootElement[0].GetRawText());
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
