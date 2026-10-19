using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Ports;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Кандидаты для строки бумаги через порт модулей (issue #1514): <c>IModuleCatalog.SimilarAsync</c>. Здесь
/// же, рядом с шагом «Новая позиция», потому что сторож у них один — загрузчик записей сверки общий, и
/// окно, сказавшее «похожих нет» о позиции, которую подбор находит, — это два загрузчика.
/// </summary>
public partial class NomenclatureIntakeTests
{
    /// <summary>
    /// Позиция, заведённая шагом «Новая», — в кандидатах при следующем же вопросе (кэша нет), и единственная
    /// с этим именем — лучшее совпадение. Ключ у записи — её собственного вида: у строки вида нет.
    /// </summary>
    [Fact]
    public async Task Порт_находит_позицию_заведённую_шагом_Новая_сразу_и_по_ключу_её_вида()
    {
        var (client, _) = await SignInAsync("Supplier");
        var kind = await KindAsync();
        var name = Unique("Реле промежуточное");
        var article = $"RP-{Guid.NewGuid().ToString("N")[..6]}";

        var response = await client.PostAsJsonAsync("/api/nomenclature", new
        {
            typeId = kind.Type,
            values = new Dictionary<string, string> { ["Наименование"] = name, ["Артикул"] = article },
            refs = new Dictionary<string, Guid> { ["ЕдиницаИзмерения"] = kind.Unit },
        });
        await OkAsync(response);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var answers = await CandidatesAsync(new ModuleLineWords(name, null), new("Что-то от поставщика", article.ToLowerInvariant()));
        Assert.Equal(2, answers.Count);

        var byName = Assert.Single(answers[0].Items, h => h.Ref.Id == id);
        Assert.True(byName.Strong);
        Assert.Equal("то же наименование", byName.Why[0]);
        Assert.Equal("ПозицияСКлючом", byName.Ref.EntityType);
        Assert.False(byName.Ref.Archived);

        // Артикул — полем ключа вида записи, с его заголовком: так причину и читает человек.
        var byCode = Assert.Single(answers[1].Items, h => h.Ref.Id == id);
        Assert.Equal($"совпадает артикул «{article.ToLowerInvariant()}»", byCode.Why[0]);
        Assert.Equal("Артикул", byCode.Matched.Field);
    }

    /// <summary>
    /// Сторож загрузчика: архивная позиция в кандидаты не встаёт и названа числом, а запись с удалённой
    /// основой — несверенной, а не «не подошла». И окно «Новая позиция» видит ту же запись тем же счётом.
    /// </summary>
    [Fact]
    public async Task Архивная_идёт_числом_а_нечитаемая_несверенной_у_порта_и_у_окна_одинаково()
    {
        var kind = await KindAsync();
        var mark = Guid.NewGuid().ToString("N")[..6];
        var line = $"Лоток {mark} 100х50 перфорированный";
        var live = await PositionAsync(kind, $"Лоток {mark} 100х50", null, null);
        var retired = await PositionAsync(kind, line, null, null);
        await SqlAsync("""UPDATE domain_objects SET "ArchivedAt" = now() WHERE "Id" = {0}""", retired);

        var answer = Assert.Single(await CandidatesAsync(new ModuleLineWords(line, null)));
        Assert.Equal([live], answer.Items.Select(h => h.Ref.Id));
        Assert.Equal(1, answer.InArchive);
        Assert.False(answer.More);

        // Основа удалена: о позиции нельзя сказать ни «подошла», ни «не подошла». База общая с соседями —
        // поэтому сравнивается прибавка, а не число.
        var before = (Assert.Single(await CandidatesAsync(new ModuleLineWords(line, null))).Unreadable, (await IntakeSimilarAsync(kind, line)).Unreadable);
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new CreateCommonDataEntryCommand(
                $"Лоток {mark} без основы", kind.Type,
                JsonDocument.Parse($$"""{"_baseRef":"{{Guid.NewGuid()}}","Наименование":"{{line}}"}"""),
                CatalogScope.System, null));

        var after = Assert.Single(await CandidatesAsync(new ModuleLineWords(line, null)));
        Assert.Equal(before.Item1 + 1, after.Unreadable);
        Assert.Equal(before.Item2 + 1, (await IntakeSimilarAsync(kind, line)).Unreadable);
        // В кандидаты нечитаемая не встаёт, хоть её название и совпадает со строкой слово в слово.
        Assert.Equal([live], after.Items.Select(h => h.Ref.Id));
    }

    [Fact]
    public async Task Порт_отдаёт_ответ_на_каждую_строку_и_режет_действующих_пределом()
    {
        var kind = await KindAsync();
        var mark = Guid.NewGuid().ToString("N")[..6];
        for (var i = 0; i < 3; i++) await PositionAsync(kind, $"Хомут {mark} нейлоновый №{i}", null, null);

        var answers = await CandidatesUpToAsync(2, new ModuleLineWords($"Хомут {mark} нейлоновый", null), new($"Ничего {mark}-нет", null));

        Assert.Equal(2, answers[0].Items.Count);
        Assert.True(answers[0].More);
        Assert.Empty(answers[1].Items);
        Assert.Empty(await CandidatesAsync());
    }

    /// <summary>
    /// Запись вида без ключа идентичности — у корня «Номенклатуры» этого хоста схема пуста — сверяется
    /// названием, а найденная альтернативным именем называет его: поиск в том же окне её находит, и подбор,
    /// молча её пропустивший, говорил бы «подходящих нет». Проверено поломкой: без чтения таких записей
    /// ответ пуст.
    /// </summary>
    [Fact]
    public async Task Запись_вида_без_ключа_находится_названием_и_альтернативным_именем()
    {
        var root = await TypeAsync(TestDirectories.NomenclatureCode, "Номенклатура");
        var mark = Guid.NewGuid().ToString("N")[..6];
        var bare = await EntryAsync(root, $"Муфта {mark} термоусаживаемая", $"ТУМ-{mark} концевая");

        var answers = await CandidatesAsync(
            new ModuleLineWords($"Муфта {mark} термоусаживаемая", null), new($"тум-{mark} концевая", null));

        var byName = Assert.Single(answers[0].Items, h => h.Ref.Id == bare);
        Assert.Equal("то же наименование", byName.Why[0]);
        Assert.Null(byName.Ref.MatchedAlias);

        var byAlias = Assert.Single(answers[1].Items, h => h.Ref.Id == bare);
        Assert.Equal("так названа альтернативным именем", byAlias.Why[0]);
        Assert.Equal($"ТУМ-{mark} концевая", byAlias.Ref.MatchedAlias);
    }

    [Fact]
    public async Task Неназначенный_справочник_ноль_а_предел_меньше_одного_отказ()
    {
        // Роль не несёт ни один тип — «справочник не назначен», а не «подходящих нет».
        var unassigned = new TestDirectory();
        using var scope = host.Services.CreateScope();
        Assert.Null(await unassigned.Port(scope.ServiceProvider)
            .SimilarAsync(unassigned.Role, [new("Что-то", null)], 5));

        var port = scope.ServiceProvider.GetRequiredService<IModuleCatalog>();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            port.SimilarAsync(CostsDirectories.Nomenclature, [new("Что-то", null)], 0));
    }

    private Task<IReadOnlyList<ModuleLineCandidates>> CandidatesAsync(params ModuleLineWords[] lines) =>
        CandidatesUpToAsync(10, lines);

    private async Task<IReadOnlyList<ModuleLineCandidates>> CandidatesUpToAsync(int limit, params ModuleLineWords[] lines)
    {
        using var scope = host.Services.CreateScope();
        var answer = await scope.ServiceProvider.GetRequiredService<IModuleCatalog>()
            .SimilarAsync(CostsDirectories.Nomenclature, lines, limit);
        Assert.NotNull(answer);
        return answer;
    }

    private async Task<SimilarAnswer> IntakeSimilarAsync((Guid Type, Guid Unit) kind, string name)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<INomenclatureIntake>()
            .SimilarAsync(kind.Type, new Dictionary<string, string?> { ["Наименование"] = name });
    }
}
