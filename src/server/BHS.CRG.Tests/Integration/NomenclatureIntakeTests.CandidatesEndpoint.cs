using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Objects;
using BHS.CRG.Modules.Costs.Data;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Адрес кандидатов модуля (issue #1515): <c>POST /api/costs/nomenclature/candidates</c> — подбор ядра и
/// соответствия ДРУГИХ поставщиков одним списком. Здесь же, рядом с портом: позиции с артикулом заводит
/// этот класс.
///
/// <para>Соответствия кладутся прямо в таблицу модуля: как они запоминаются, проверяет
/// <see cref="SupplierMatchTests" />, а здесь важно только, что они есть.</para>
/// </summary>
public partial class NomenclatureIntakeTests
{
    /// <summary>
    /// Читатель документов без прав справочника получает кандидатов — окно выбора есть и в счёте, и в
    /// накладной; модуль открыт, а документов не читает — отказ.
    /// </summary>
    [Fact]
    public async Task Кандидатов_получает_читатель_счетов_или_накладных_без_прав_справочника()
    {
        var body = new { lines = new[] { new { supplierText = Unique("Кабель"), supplierCode = (string?)null } } };

        foreach (var right in new[] { "costs.invoice.read", "costs.waybill.read" })
        {
            var (reader, _) = await SignInAsync(await RoleAsync(right));
            await OkAsync(await reader.PostAsJsonAsync("/api/costs/nomenclature/candidates", body));
        }

        var (keeper, _) = await SignInAsync(await RoleAsync("costs.articles.edit"));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await keeper.PostAsJsonAsync("/api/costs/nomenclature/candidates", body)).StatusCode);
    }

    /// <summary>
    /// Одна позиция из артикула ядра и из чужого выбора — одна строка с обеими причинами, артикул первым; и
    /// «лучшее» остаётся: чужой выбор указал на неё же.
    /// </summary>
    [Fact]
    public async Task Артикул_и_чужой_выбор_одной_позиции_одна_строка_с_двумя_причинами()
    {
        var kind = await KindAsync();
        var article = Unique("ВА47-29").Replace(' ', '-');
        var position = await PositionAsync(kind, Unique("Автомат"), null, article);
        var (other, name) = await VendorAsync();
        await MatchAsync(other, SupplierMatchKind.Code, article, position);

        var item = Assert.Single(Items(Assert.Single(await CandidatesAsync(await VendorIdAsync(), ("Выключатель от продавца", article)))));

        Assert.Equal(position, item.GetProperty("id").GetGuid());
        Assert.Equal(
            [$"совпадает артикул «{article}»", $"так выбрали у «{name}» по артикулу «{article}»"],
            item.GetProperty("why").EnumerateArray().Select(w => w.GetString()));
        Assert.True(item.GetProperty("strong").GetBoolean());
    }

    /// <summary>
    /// Чужой выбор ДРУГОЙ позиции — слабый кандидат после артикула, и «лучшее» ядра он снимает: у строки два
    /// ответа. Слов строки с ним не сверяли — сравнения нет.
    /// </summary>
    [Fact]
    public async Task Чужой_выбор_другой_позиции_слабый_и_снимает_лучшее()
    {
        var kind = await KindAsync();
        var article = Unique("00-0029").Replace(' ', '-');
        var byCode = await PositionAsync(kind, Unique("Розетка"), null, article);
        var chosen = await PositionAsync(kind, Unique("Выключатель"), null, null);
        await MatchAsync((await VendorAsync()).Id, SupplierMatchKind.Code, article, chosen);

        var items = Items(Assert.Single(await CandidatesAsync(null, ("Что-то из 1С продавца", article))));

        Assert.Equal([byCode, chosen], items.Select(i => i.GetProperty("id").GetGuid()));
        Assert.All(items, i => Assert.False(i.GetProperty("strong").GetBoolean()));
        Assert.StartsWith("так выбрали у «", items[1].GetProperty("why")[0].GetString());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("matched").ValueKind);
    }

    /// <summary>
    /// Свой поставщик кандидатом не идёт — его запомненное подставляет форма; запомненное в архиве названо
    /// отдельно. Чужой выбор архивной позиции живым не показан.
    /// </summary>
    [Fact]
    public async Task Своё_запомненное_в_архиве_названо_а_чужой_выбор_архивной_не_показан()
    {
        var kind = await KindAsync();
        var line = Unique("Щит распределительный");
        var (mine, _) = await VendorAsync();
        var retired = await PositionAsync(kind, Unique("Щит старый"), null, null);
        var gone = await PositionAsync(kind, Unique("Щит снятый"), null, null);
        await MatchAsync(mine, SupplierMatchKind.Name, line, retired);
        await MatchAsync((await VendorAsync()).Id, SupplierMatchKind.Name, line, gone);
        await ArchiveAsync(retired);
        await ArchiveAsync(gone);

        var answer = Assert.Single(await CandidatesAsync(mine, (line, null)));

        // Справочник общий с соседями — щиты бывают и у них: смотрим только на свои позиции.
        Assert.DoesNotContain(Items(answer), i => i.GetProperty("id").GetGuid() is var id && (id == retired || id == gone));
        var unusable = answer.GetProperty("recalledUnusable");
        Assert.Equal(retired, unusable.GetProperty("id").GetGuid());
        Assert.Equal("archived", unusable.GetProperty("issue").GetString());
        Assert.StartsWith("Щит старый", unusable.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Две_позиции_с_одним_артикулом_ни_одной_лучшей()
    {
        var kind = await KindAsync();
        var article = Unique("PR-16").Replace(' ', '-');
        await PositionAsync(kind, Unique("Реле"), null, article);
        await PositionAsync(kind, Unique("Реле"), null, article);

        var items = Items(Assert.Single(await CandidatesAsync(null, ("Реле времени", article))));

        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.False(i.GetProperty("strong").GetBoolean()));
    }

    /// <summary>
    /// Три места окна — три формы строки: счёт (наименование и артикул), накладная (без артикула),
    /// «Соответствия» (одно поле). Отвечают все, а картинки позиции в ответ не едут.
    /// </summary>
    [Fact]
    public async Task Отвечают_все_три_формы_строки_и_картинок_в_ответе_нет()
    {
        var kind = await KindAsync();
        var name = Unique("Светильник");
        var article = Unique("LED-36").Replace(' ', '-');
        var position = await PositionAsync(kind, name, null, article);
        await SqlAsync("""
            UPDATE domain_objects SET "Data" = jsonb_set("Data", '{{Изображение}}', '"data:image/png;base64,iVBORw0KGgo="')
            WHERE "Id" = {0}
            """, position);

        var (client, _) = await SignInAsync("Supplier");
        var response = await client.PostAsJsonAsync("/api/costs/nomenclature/candidates", new
        {
            lines = new object[]
            {
                new { supplierText = name, supplierCode = article },
                new { supplierText = name, supplierCode = (string?)null },
                new { supplierText = (string?)null, supplierCode = article },
            },
        });
        await OkAsync(response);
        var text = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("data:", text);
        var lines = JsonDocument.Parse(text).RootElement.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(3, lines.Count);
        Assert.All(lines, l => Assert.Contains(Items(l), i => i.GetProperty("id").GetGuid() == position));
    }

    [Fact]
    public async Task Без_строк_отказ_а_больше_предела_отказ_с_числом()
    {
        var (client, _) = await SignInAsync("Supplier");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/costs/nomenclature/candidates", new { supplierId = (Guid?)null })).StatusCode);

        var many = await client.PostAsJsonAsync("/api/costs/nomenclature/candidates", new
        {
            lines = Enumerable.Range(0, 51).Select(i => new { supplierText = $"Строка {i}" }),
        });
        Assert.Equal(HttpStatusCode.BadRequest, many.StatusCode);
        Assert.Contains("51", await many.Content.ReadAsStringAsync());
    }

    private static List<JsonElement> Items(JsonElement answer) => [.. answer.GetProperty("items").EnumerateArray()];

    private async Task<List<JsonElement>> CandidatesAsync(Guid? supplier, params (string? Text, string? Code)[] lines)
    {
        var (client, _) = await SignInAsync("Supplier");
        var response = await client.PostAsJsonAsync("/api/costs/nomenclature/candidates", new
        {
            supplierId = supplier,
            lines = lines.Select(l => new { supplierText = l.Text, supplierCode = l.Code }),
        });
        await OkAsync(response);
        return [.. (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lines").EnumerateArray()];
    }

    private async Task<(Guid Id, string Name)> VendorAsync()
    {
        var name = $"ООО «Поставщик {Guid.NewGuid().ToString("N")[..6]}»";
        return (await EntryAsync(await TypeAsync(TestDirectories.OrganizationCode, "Организация"), name), name);
    }

    private async Task<Guid> VendorIdAsync() => (await VendorAsync()).Id;

    private async Task MatchAsync(Guid supplier, SupplierMatchKind kind, string text, Guid position)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        db.SupplierMatches.Add(SupplierMatch.Create(supplier, SupplierMatchKey.Of(kind, text)!, position, null, null));
        await db.SaveChangesAsync();
    }

    private async Task ArchiveAsync(Guid id)
    {
        using var scope = host.Services.CreateScope();
        Assert.Equal(ArchiveOutcome.Changed,
            await scope.ServiceProvider.GetRequiredService<IRecordArchive>().SetAsync(id, archived: true));
    }
}
