using BHS.CRG.Application.Documents;

namespace BHS.CRG.Tests.Documents;

/// <summary>
/// Кандидаты для строки бумаги (issue #1513, эпик #1511) — без базы: артикул, то же наименование,
/// название позиции целиком в строке, большинство слов; «сильный — только единственный».
/// </summary>
public class NomenclatureLineCandidatesTests
{
    private static readonly Guid Kind = Guid.NewGuid();

    private static readonly Dictionary<Guid, IReadOnlyList<string>> Titles =
        new() { [Kind] = ["Наименование", "Производитель", "Артикул"] };

    private static SimilarRecord Lying(string name, string? maker = null, string? article = null, bool archived = false,
        string[]? aliases = null, bool ownKey = false) =>
        new(Guid.NewGuid(), Kind, "Номенклатура", name, archived, [name, maker, article], aliases ?? [], ownKey);

    private static LineAnswer For(IReadOnlyList<SimilarRecord> records, string? name, string? code = null) =>
        NomenclatureSimilarity.ForLine(new(name, code), records, Titles);

    [Fact]
    public void Редкий_артикул_сильный_и_причина_называет_поле()
    {
        var right = Lying("Автомат C10", "IEK", "MCB10-1-06C");
        var other = Lying("Автомат C16", "IEK", "MCB10-1-16C");

        var hit = Assert.Single(For([right, other], "Выключатель автоматический ВА47-29 1P", "mcb10-1-06c ").Hits);

        Assert.Equal(right.Id, hit.Record.Id);
        Assert.True(hit.Strong);
        Assert.Equal("совпадает артикул «mcb10-1-06c»", hit.Why[0]);
        Assert.Equal("Артикул", hit.Matched.Field);
    }

    [Fact]
    public void Артикул_у_нескольких_позиций_не_сильный_и_причина_просит_сверить()
    {
        var three = Enumerable.Range(0, 3).Select(i => Lying($"Изделие {i}", article: "00-0029")).ToList();

        var answer = For(three, "Что-то от поставщика", "00-0029");

        Assert.Equal(3, answer.Hits.Count);
        Assert.All(answer.Hits, h => Assert.False(h.Strong));
        Assert.All(answer.Hits, h => Assert.Equal("артикул «00-0029» совпадает у 3 позиций — сверьте", h.Why[0]));
    }

    [Fact]
    public void Частое_значение_поля_ключа_не_артикул()
    {
        // «IEK» у сотни позиций позицию не отличает — кандидатом её делает не он.
        var crowd = Enumerable.Range(0, NomenclatureSimilarity.RareLimit + 1)
            .Select(i => Lying($"Изделие {i}", "IEK")).ToList();

        Assert.Empty(For(crowd, "Совсем другое наименование", "IEK").Hits);
    }

    [Fact]
    public void То_же_наименование_сильное_а_у_двух_позиций_ни_одно()
    {
        var one = Lying("Кабель ВВГнг 3х2,5");

        var hit = Assert.Single(For([one], " кабель  ВВГНГ 3х2,5. ").Hits);
        Assert.True(hit.Strong);
        Assert.Equal(["то же наименование"], hit.Why);

        var twins = For([one, Lying("Кабель ВВГнг 3х2,5", "Севкабель")], "Кабель ВВГнг 3х2,5");
        Assert.Equal(2, twins.Hits.Count);
        Assert.All(twins.Hits, h => Assert.False(h.Strong));
    }

    [Fact]
    public void Альтернативное_имя_тоже_наименование()
    {
        var named = Lying("Кабель ВВГнг(А)-LS 3х2,5", aliases: ["ВВГ 3*2.5"]);

        var hit = Assert.Single(For([named], "ввг 3*2.5").Hits);

        Assert.True(hit.Strong);
        Assert.Equal(["так названа альтернативным именем"], hit.Why);
    }

    [Fact]
    public void Артикул_у_одной_и_имя_у_другой_сильного_нет()
    {
        // Бумага говорит про две разные позиции — Enter выбрал бы одну из них наугад.
        var byCode = Lying("Панель монтажная 71", article: "mb-71-24");
        var byName = Lying("Панель ЩРН-24");

        var answer = For([byCode, byName], "Панель ЩРН-24", "mb-71-24");

        Assert.Equal(2, answer.Hits.Count);
        Assert.All(answer.Hits, h => Assert.False(h.Strong));
        Assert.Equal(byCode.Id, answer.Hits[0].Record.Id);
    }

    [Fact]
    public void Одна_позиция_по_двум_правилам_одна_строка_со_всеми_причинами()
    {
        var both = Lying("Автомат C10", "IEK", "MCB10-1-06C");

        var hit = Assert.Single(For([both], "Автомат C10", "MCB10-1-06C").Hits);

        Assert.Equal(["совпадает артикул «MCB10-1-06C»", "то же наименование"], hit.Why);
        Assert.True(hit.Strong);
    }

    [Fact]
    public void Длинная_бумага_находит_короткую_позицию_названием_целиком()
    {
        var panel = Lying("Панель монтажная 71");

        // Из девяти слов строки в названии три — «большинство» молчало бы.
        var hit = Assert.Single(For([panel], "Панель монтажная EKF 71 для щита ЩРН-24 (код mb-71-24)").Hits);

        Assert.Equal(["название позиции целиком есть в строке"], hit.Why);
        Assert.False(hit.Strong);
    }

    [Fact]
    public void Одно_общее_слово_не_кандидат()
    {
        var bare = Lying("Кабель");
        var other = Lying("Кабель КВВГ 10х1,5");

        Assert.Empty(For([bare, other], "Кабель ВВГнг 3х2,5").Hits);
    }

    [Fact]
    public void Большинство_слов_строки_как_у_похожих()
    {
        var close = Lying("Кабель силовой ВВГнг(А)-LS 3х2,5 ГОСТ");

        var hit = Assert.Single(For([close], "Кабель ВВГнг 3х2,5").Hits);

        Assert.Equal(["большинство слов строки"], hit.Why);
    }

    [Fact]
    public void Совпавшие_слова_строки_отмечены_как_написаны()
    {
        var cable = Lying("Кабель ВВГ 3х2,5");

        var hit = Assert.Single(For([cable], "Кабель  ВВГнг 3х2,5 медный").Hits);

        Assert.Equal(
            new LineWord[] { new("Кабель", 0, true), new("ВВГнг", 8, true), new("3х2,5", 14, true), new("медный", 20, false) },
            hit.Matched.Words);
        Assert.Null(hit.Matched.Field);
    }

    /// <summary>Место слова — в строке поставщика как она есть: со скобками и переводом строки.</summary>
    [Fact]
    public void Слово_строки_названо_местом_в_исходном_тексте()
    {
        const string paper = "Кабель ВВГнг(А)-LS\t3х2,5 (код mb-71)";

        var hit = Assert.Single(For([Lying("Кабель ВВГнг 3х2,5")], paper).Hits);

        Assert.All(hit.Matched.Words, w => Assert.Equal(w.Text, paper.Substring(w.Start, w.Text.Length)));
        Assert.Equal(["Кабель", "ВВГнг", "3х2,5"], hit.Matched.Words.Where(w => w.Found).Select(w => w.Text));
    }

    [Fact]
    public void Архивная_не_сильная_и_идёт_после_действующей()
    {
        var archived = Lying("Лоток 100х50", archived: true);

        Assert.False(Assert.Single(For([archived], "Лоток 100х50").Hits).Strong);

        var live = Lying("Лоток 100х50 перфорированный");
        var answer = For([archived, live], "Лоток 100х50 перфорированный");
        Assert.Equal(live.Id, answer.Hits[0].Record.Id);
    }

    /// <summary>
    /// Позицию заменили, старую убрали в архив: единственная ДЕЙСТВУЮЩАЯ остаётся лучшим совпадением —
    /// и по имени, и по артикулу. Проверено поломкой: со счётом архивных сильного здесь нет.
    /// </summary>
    [Fact]
    public void Архивный_двойник_не_отнимает_лучшее_совпадение_у_действующей()
    {
        var old = Lying("Лоток 100х50", article: "LT-100", archived: true);
        var live = Lying("Лоток 100х50", article: "LT-100");

        var byName = For([old, live], "Лоток 100х50");
        Assert.True(byName.Hits.Single(h => h.Record.Id == live.Id).Strong);
        Assert.False(byName.Hits.Single(h => h.Record.Id == old.Id).Strong);

        var byCode = For([old, live], "Лоток металлический", "LT-100");
        var hit = byCode.Hits.Single(h => h.Record.Id == live.Id);
        Assert.True(hit.Strong);
        Assert.Equal("совпадает артикул «LT-100»", hit.Why[0]);
        // Архивной сказано честно: артикул у неё не один.
        Assert.StartsWith("артикул «LT-100» совпадает у 2 позиций", byCode.Hits.Single(h => h.Record.Id == old.Id).Why[0]);
    }

    /// <summary>
    /// D и E — один уровень, решает доля слов строки: общая «Кабель ВВГ» (D — оба её слова в строке) не
    /// встаёт выше позиции, совпавшей со строкой во всех словах. Проверено поломкой: с D уровнем выше
    /// первой шла общая.
    /// </summary>
    [Fact]
    public void Общая_позиция_целиком_в_строке_не_выше_совпавшей_во_всех_словах()
    {
        var generic = Lying("Кабель ВВГ");
        var exact = Lying("Кабель силовой ВВГнг(А)-LS 3х2,5 ГОСТ");

        var answer = For([generic, exact], "Кабель ВВГнг(А)-LS 3х2,5");

        Assert.Equal([exact.Id, generic.Id], answer.Hits.Select(h => h.Record.Id));
        Assert.Equal("название позиции целиком есть в строке", answer.Hits[1].Why[0]);
    }

    /// <summary>
    /// Слово строки, продолжающее слово позиции («Короба» — «Короб»), засчитано правилом и подсвечено
    /// одинаково: подсветка не обещает того, по чему не подбирали.
    /// </summary>
    [Fact]
    public void Подсвечено_ровно_то_что_засчитано()
    {
        var box = Lying("Короб 40х25 ДКС");

        var hit = Assert.Single(For([box], "Короба 40х25 пластиковые").Hits);

        Assert.Equal(["большинство слов строки"], hit.Why);
        Assert.Equal(["Короба", "40х25"], hit.Matched.Words.Where(w => w.Found).Select(w => w.Text));
    }

    [Fact]
    public void Название_целиком_находится_и_альтернативным_именем()
    {
        var shield = Lying("Щиток", aliases: ["Щит распределительный ЩРН-12"]);

        var hit = Assert.Single(For([shield], "Щит распределительный ЩРН-12 навесной IP31").Hits);

        Assert.Equal(["название позиции целиком есть в строке"], hit.Why);
        Assert.Equal(3, hit.Matched.Words.Count(w => w.Found));
    }

    /// <summary>Запись, у которой в ключе одно название, с артикулом строки сверяется без отказа.</summary>
    [Fact]
    public void Ключ_из_одного_названия_и_артикул_строки()
    {
        var bare = new SimilarRecord(Guid.NewGuid(), Kind, "Номенклатура", "Реле РП-21", false, ["Реле РП-21"], []);
        var empty = new SimilarRecord(Guid.NewGuid(), Kind, "Номенклатура", "Реле РП-21", false, [], []);

        var answer = For([bare, empty], "Реле РП-21", "RP-21");

        Assert.Equal(2, answer.Hits.Count);
        Assert.All(answer.Hits, h => Assert.Equal(["то же наименование"], h.Why));
    }

    [Fact]
    public void Позиция_со_своими_полями_ключа_кандидат_но_сверить_глазами()
    {
        var cable = Lying("Кабель ВВГ", ownKey: true);

        var hit = Assert.Single(For([cable], "Кабель ВВГ").Hits);

        Assert.False(hit.Strong);
        Assert.Contains("сверьте по карточке", hit.Why[^1]);
    }

    [Fact]
    public void Пустая_строка_пустой_ответ_а_несверенные_названы()
    {
        var answer = NomenclatureSimilarity.ForLine(new("  ", null), [Lying("Реле")], Titles, unreadable: 2);

        Assert.Empty(answer.Hits);
        Assert.Equal(2, answer.Unreadable);
    }

    [Fact]
    public void Вид_без_заголовков_поле_ключа()
    {
        var foreign = new SimilarRecord(Guid.NewGuid(), Guid.NewGuid(), "Прочее", "Реле", false, ["Реле", "РП-21"], []);

        var hit = Assert.Single(For([foreign], "Что-то", "рп-21").Hits);

        Assert.Equal("поле ключа", hit.Matched.Field);
    }

    [Fact]
    public void Кандидатов_больше_предела_сказано_и_ответ_один_и_тот_же()
    {
        var many = Enumerable.Range(0, NomenclatureSimilarity.Limit + 3).Select(_ => Lying("Реле прогона")).ToList();

        var answer = For(many, "Реле прогона");
        var again = For([.. many.AsEnumerable().Reverse()], "Реле прогона");

        Assert.Equal(NomenclatureSimilarity.Limit, answer.Hits.Count);
        Assert.True(answer.More);
        Assert.Equal(answer.Hits.Select(h => h.Record.Id), again.Hits.Select(h => h.Record.Id));
    }
}
