using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Objects.FieldSuggestions;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Persistence.Configurations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// ПЛАН запроса подсказок, а не ответ (issue #1530, D2): неверная форма отдаёт тот же список, только в
/// тридцать раз дольше, и тест ответа этого не увидит.
///
/// <para>Тип — тысяча записей с «картинкой» (случайный текст, сжатию не поддаётся) каждая; окно по объёму
/// — один мегабайт, то есть почти все записи вне окна. Проверяется наличие узлов, а не стоимость: она
/// зависит от статистики тестовой базы.</para>
/// </summary>
[Collection("Integration")]
public partial class FieldSuggestionPlanTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    private readonly InvoiceLineHost host = host;

    private const int Records = 1000;

    /// <summary>Записей типа сверх тяжёлых — старых и лёгких, как давние позиции справочника.</summary>
    private const int Old = 9000;

    /// <summary>
    /// Предел записей — настоящий, объём — один мегабайт: тысяча записей по ~20 КБ режется объёмом
    /// раньше, чем числом. Записей у типа вдесятеро больше предела — как у заказчика (50 тысяч): при
    /// равных числах сортировка всех записей типа не дороже выборки по индексу, база законно выбирает
    /// её, и тест проверял бы совпадение двух чисел, а не форму запроса.
    /// </summary>
    private static readonly SuggestionWindow Window =
        SuggestionWindow.Default with { MaxBytes = 1024 * 1024, Timeout = TimeSpan.FromSeconds(30) };

    [GeneratedRegex(@"Index Scan using ""PK_domain_objects""[^\n]*loops=(\d+)")]
    private static partial Regex FieldReads();

    /// <summary>Строк, прочитанных индексом окна, — у каждой ветки семьи свой узел.</summary>
    [GeneratedRegex(@"Index Scan using ""IX_domain_objects_CompositeTypeId_UpdatedAt_Live""[^\n]*rows=(\d+)")]
    private static partial Regex WindowReads();

    [Fact]
    public async Task Окно_берётся_индексом_а_поле_читается_только_у_записей_окна()
    {
        var type = await HeavyTypeAsync();

        var plan = await ExplainAsync(type);

        // План — в сообщении: без него красный тест говорил бы «чего-то нет», не показывая, что есть.
        Assert.True(plan.Contains($"Index Scan using \"{DomainObjectConfiguration.FieldSuggestionsIndex}\""), plan);
        Assert.True(plan.Contains("Limit"), plan);
        Assert.False(plan.Contains("Seq Scan on domain_objects"), plan);

        // Поле прочитано ровно у записей окна — по одному обращению на запись, и окно отрезал объём, а
        // не число: обращений меньше предела записей.
        var reads = int.Parse(Assert.Single(FieldReads().Matches(plan)).Groups[1].Value);
        using var scope = host.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<IFieldSuggestionReader>()
            .ReadAsync([type], "Производитель", Window);
        Assert.Equal(rows.Count, reads);
        Assert.InRange(reads, 1, Window.MaxRecords - 1);
    }

    /// <summary>
    /// Семья из нескольких больших типов читает окно, а не по окну на тип: ветки сливаются по свежести
    /// и останавливаются на пределе записей. Сортировка всех веток вместо слияния прочитала бы предел
    /// записей У КАЖДОГО типа — у справочника с десятками видов это десятки тысяч чтений на букву.
    /// </summary>
    [Fact]
    public async Task Семья_из_нескольких_типов_читает_одно_окно_а_не_окно_на_тип()
    {
        var family = new List<Guid>();
        for (var i = 0; i < 3; i++) family.Add(await LightTypeAsync(SuggestionWindow.Default.MaxRecords + 100));

        var plan = await ExplainAsync(family, SuggestionWindow.Default);

        var read = WindowReads().Matches(plan).Sum(m => int.Parse(m.Groups[1].Value));
        Assert.True(read <= SuggestionWindow.Default.MaxRecords + family.Count, $"Прочитано {read}:\n{plan}");
    }

    /// <summary>
    /// Самая свежая запись тяжелее предела по объёму не обнуляет окно (D10): без неё нарастающая сумма
    /// с первой строки выше предела, и адрес отвечал бы «пусто» у каждого поля типа.
    /// </summary>
    [Fact]
    public async Task Запись_тяжелее_предела_объёма_всё_равно_в_окне()
    {
        var type = await LightTypeAsync(3);
        using var scope = host.Services.CreateScope();

        var rows = await scope.ServiceProvider.GetRequiredService<IFieldSuggestionReader>()
            .ReadAsync([type], "Производитель", Window with { MaxBytes = 1 });

        Assert.Equal("Лёгкий 3", Assert.Single(rows).Value);
    }

    private async Task<string> ExplainAsync(Guid type) => await ExplainAsync([type], Window);

    private async Task<string> ExplainAsync(IReadOnlyCollection<Guid> types, SuggestionWindow window)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using (var analyze = new NpgsqlCommand("ANALYZE domain_objects", connection))
            await analyze.ExecuteNonQueryAsync();

        await using var cmd = new NpgsqlCommand("EXPLAIN (ANALYZE, COSTS OFF) " + FieldSuggestionReader.Sql(types.Count), connection);
        FieldSuggestionReader.AddParameters(cmd, types, "Производитель", window);
        var lines = new StringBuilder();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) lines.AppendLine(reader.GetString(0));
        return lines.ToString();
    }

    /// <summary>Тип с тысячей записей по ~20 КБ несжимаемых данных.</summary>
    private async Task<Guid> HeavyTypeAsync()
    {
        using var scope = host.Services.CreateScope();
        var types = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
        var mark = Guid.NewGuid().ToString("N")[..8];
        var type = DocumentType.Create($"Тяжёлый {mark}", $"Тяжёлый{mark}", DocumentTypeKind.Composite, null,
            JsonDocument.Parse("""{"fields":[{"key":"Производитель","type":"string"},{"key":"Изображение","type":"string"}]}"""),
            TypeOwner.Core, TypeVisibility.Shared);
        await types.AddAsync(type);
        await types.SaveChangesAsync();

        var objects = scope.ServiceProvider.GetRequiredService<IRepository<DomainObject>>();
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < Records; i++)
        {
            var noise = string.Concat(Enumerable.Range(0, 400).Select(_ => Convert.ToBase64String(Guid.NewGuid().ToByteArray())));
            var data = JsonSerializer.SerializeToDocument(new Dictionary<string, string>
            {
                ["Производитель"] = $"Производитель {i % 7}",
                ["Изображение"] = noise,
            });
            await objects.AddAsync(DomainObject.Restore(Guid.NewGuid(), type.Id, $"Запись {i}", data,
                CatalogScope.System, null, at.AddMinutes(i), at.AddMinutes(i), null, archivedAt: null));
        }
        await objects.SaveChangesAsync();

        // Старые лёгкие записи — одним запросом: по одной через EF их заводить дольше самого теста.
        await InsertLightAsync(type.Id, Old, at, -1, "Старый");
        return type.Id;
    }

    /// <summary>Тип с <paramref name="count" /> лёгкими записями; самая свежая — «Лёгкий {count}».</summary>
    private async Task<Guid> LightTypeAsync(int count)
    {
        using var scope = host.Services.CreateScope();
        var types = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
        var mark = Guid.NewGuid().ToString("N")[..8];
        var type = DocumentType.Create($"Лёгкий {mark}", $"Лёгкий{mark}", DocumentTypeKind.Composite, null,
            JsonDocument.Parse("""{"fields":[{"key":"Производитель","type":"string"}]}"""),
            TypeOwner.Core, TypeVisibility.Shared);
        await types.AddAsync(type);
        await types.SaveChangesAsync();
        await InsertLightAsync(type.Id, count, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 1, "Лёгкий");
        return type.Id;
    }

    /// <summary>
    /// Лёгкие записи одним запросом: по одной через EF их заводить дольше самого теста. Запись № g правлена
    /// в <paramref name="at" /> + <paramref name="step" />·g минут, значение поля — «{prefix} {g}» (у старых
    /// — по модулю 5: повторы, как у давних позиций справочника).
    /// </summary>
    private async Task InsertLightAsync(Guid type, int count, DateTimeOffset at, int step, string prefix)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var old = new NpgsqlCommand("""
            INSERT INTO domain_objects ("Id", "CompositeTypeId", "DisplayName", "Aliases", "Data", "ScopeLevel",
                                        "CreatedAt", "UpdatedAt")
            SELECT gen_random_uuid(), @type, @prefix || ' ' || g, '{}',
                   jsonb_build_object('Производитель', @prefix || ' ' || CASE WHEN @step < 0 THEN g % 5 ELSE g END),
                   'System', @at + g * @step * interval '1 minute', @at + g * @step * interval '1 minute'
            FROM generate_series(1, @count) AS g
            """, connection);
        old.Parameters.AddWithValue("type", type);
        old.Parameters.AddWithValue("at", at);
        old.Parameters.AddWithValue("count", count);
        old.Parameters.AddWithValue("step", step);
        old.Parameters.AddWithValue("prefix", prefix);
        await old.ExecuteNonQueryAsync();
    }

    /// <summary>Строка подключения хоста — у него самого, а не собранная заново по его суффиксу.</summary>
    private string ConnectionString =>
        host.Services.GetRequiredService<IConfiguration>().GetConnectionString("Postgres")!;
}
