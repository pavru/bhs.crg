using BHS.CRG.Application.Documents;

namespace BHS.CRG.Tests.Documents;

/// <summary>
/// Правила «похожих» для новой позиции номенклатуры (issue #1079, ТЗ TYPE-8) — без базы: тот же ключ,
/// редкое значение поля ключа, слова названия.
/// </summary>
public class NomenclatureSimilarityTests
{
    private static readonly string[] Titles = ["Наименование", "Производитель", "Артикул"];

    private static SimilarRecord Lying(string name, string? maker = null, string? article = null, bool archived = false) =>
        new(Guid.NewGuid(), Guid.Empty, name, archived, [name, maker, article]);

    private static SimilarAnswer Find(IReadOnlyList<SimilarRecord> records, string name, string? maker = null, string? article = null) =>
        NomenclatureSimilarity.Find(
            [(Titles[0], name), (Titles[1], maker), (Titles[2], article)], records);

    [Fact]
    public void Пустое_поле_ключа_тоже_значение_и_регистр_с_точкой_ключа_не_меняют()
    {
        var bare = Lying("Кабель ВВГнг 3х2,5");
        var branded = Lying("Кабель ВВГнг 3х2,5", "Камкабель");

        Assert.Equal(bare.Id, Find([bare, branded], " кабель  ввгнг 3х2,5. ").Exact?.Id);
        Assert.Equal(branded.Id, Find([bare, branded], "Кабель ВВГнг 3х2,5", "КАМКАБЕЛЬ").Exact?.Id);
        // То же название, но производитель другой: это не двойник — это «похожая».
        var other = Find([bare, branded], "Кабель ВВГнг 3х2,5", "Севкабель");
        Assert.Null(other.Exact);
        Assert.All(other.Similar, h => Assert.Contains("отличается остальное", h.Why));
        Assert.Equal(2, other.Similar.Count);
    }

    [Fact]
    public void Запись_без_данных_сверяется_по_своему_названию()
    {
        var empty = new SimilarRecord(Guid.NewGuid(), Guid.Empty, "Реле РП-21", false, [null, null, null]);

        Assert.Equal(empty.Id, Find([empty], "реле рп-21").Exact?.Id);
    }

    [Fact]
    public void Из_двойников_предлагается_действующий_а_не_архивный()
    {
        var archived = Lying("Лоток 100х50", archived: true);
        var live = Lying("Лоток 100х50");

        Assert.Equal(live.Id, Find([archived, live], "Лоток 100х50").Exact?.Id);
        Assert.True(Find([archived], "Лоток 100х50").Exact?.Archived);
    }

    [Fact]
    public void Редкое_значение_поля_ключа_сходство_а_частое_нет()
    {
        var twin = Lying("Автомат C16", "IEK", "MVA20-1-016-C");
        var crowd = Enumerable.Range(0, NomenclatureSimilarity.RareLimit)
            .Select(i => Lying($"Изделие {i}", "IEK")).ToList();

        var answer = Find([twin, .. crowd], "Выключатель автоматический", "IEK", "mva20-1-016-c");

        var hit = Assert.Single(answer.Similar);
        Assert.Equal(twin.Id, hit.Record.Id);
        Assert.Contains("артикул", hit.Why);
    }

    [Fact]
    public void Слова_названия_большинство_а_одно_общее_слово_из_многих_не_сходство()
    {
        var close = Lying("Кабель ВВГнг(А)-LS 3х2,5");
        var far = Lying("Кабель КВВГ 10х1,5");

        var answer = Find([close, far], "Кабель силовой ВВГнг 3х2,5");

        Assert.Equal(close.Id, Assert.Single(answer.Similar).Record.Id);
    }

    [Fact]
    public void Похожих_больше_предела_сказано_а_не_обрезано_молча()
    {
        var many = Enumerable.Range(0, NomenclatureSimilarity.Limit + 3).Select(i => Lying($"Реле прогона {i:00}")).ToList();

        var answer = Find(many, "Реле прогона новое");

        Assert.Equal(NomenclatureSimilarity.Limit, answer.Similar.Count);
        Assert.True(answer.More);
        Assert.False(Find([], "Реле").More);
    }
}
