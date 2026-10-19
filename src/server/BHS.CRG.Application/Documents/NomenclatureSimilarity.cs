using BHS.CRG.Application.QualityDocs;

namespace BHS.CRG.Application.Documents;

/// <summary>
/// Лежащая позиция глазами сверки: название, альтернативные имена и значения полей ключа в порядке
/// ключа.
/// </summary>
/// <param name="Type">Название вида — для показа.</param>
/// <param name="Identity">Значения полей ключа; <c>null</c> — поля у записи нет или оно пустое.</param>
/// <param name="Aliases">Альтернативные имена записи: поиск в том же окне по ним ищет, и сверка,
/// слепая к ним, отвечала бы «похожих нет» о позиции, которую поиск находит.</param>
/// <param name="OwnKey">У записи заполнены СОБСТВЕННЫЕ поля ключа её подтипа, которых у выбранного
/// вида нет. Такая запись не может быть «той же»: кабели одного названия и разного сечения — разные
/// позиции, и совпадение общих полей ключа их не уравнивает.</param>
public sealed record SimilarRecord(
    Guid Id, Guid TypeId, string Type, string? Name, bool Archived, IReadOnlyList<string?> Identity,
    IReadOnlyList<string> Aliases, bool OwnKey = false);

/// <summary>Похожая позиция и чем она похожа — словами для человека.</summary>
public sealed record SimilarHit(SimilarRecord Record, string Why);

/// <summary>
/// Ответ о похожих.
/// </summary>
/// <param name="Exact">Позиция с ТЕМ ЖЕ ключом идентичности. Есть — вторую такую не заводят.</param>
/// <param name="More">Похожих больше, чем показано.</param>
/// <param name="Unreadable">Сколько позиций сверить НЕ УДАЛОСЬ (основа записи удалена и подобное).
/// Это не «не похожи»: о них ответа нет, и окно обязано это сказать.</param>
public sealed record SimilarAnswer(SimilarRecord? Exact, IReadOnlyList<SimilarHit> Similar, bool More, int Unreadable);

/// <summary>
/// Строка бумаги (счёта, накладной, соответствия поставщика) глазами подбора: слова поставщика и его
/// артикул. Вида у строки нет — поэтому и сверка другая, чем у новой позиции.
/// </summary>
public sealed record LineQuestion(string? Name, string? Code);

/// <summary>Что именно совпало — для сравнения «строка ↔ позиция» в карточке.</summary>
/// <param name="Words">Слова строки по порядку, как написаны; <c>Found</c> — слово есть в названии
/// позиции (или в том альтернативном имени, которым она нашлась). Разбивает на слова только сервер:
/// клиент, повторивший разбиение, подсветил бы не то, по чему подбирали.</param>
/// <param name="Field">Заголовок поля ключа, с которым совпал артикул строки; <c>null</c> — не совпал.</param>
public sealed record LineMatched(IReadOnlyList<LineWord> Words, string? Field);

/// <summary>Слово строки бумаги так, как оно в ней написано.</summary>
/// <param name="Start">Где слово начинается в исходной строке (<see cref="LineQuestion.Name" />): карточка
/// подсвечивает его в тексте поставщика как есть — со скобками, косыми и кавычками, которых в словах нет.</param>
public sealed record LineWord(string Text, int Start, bool Found);

/// <summary>Кандидат для строки бумаги.</summary>
/// <param name="Why">Причины одной фразой каждая, старшая первой.</param>
/// <param name="Strong">«Лучшее совпадение»: артикул или то же наименование — и такая позиция ОДНА.
/// Только его окно выбирает по Enter, поэтому признак ставит функция, а не экран по тексту причины.</param>
public sealed record LineHit(SimilarRecord Record, IReadOnlyList<string> Why, bool Strong, LineMatched Matched);

/// <summary>Ответ подбора для строки.</summary>
/// <param name="More">Кандидатов больше, чем показано.</param>
/// <param name="Unreadable">Сколько позиций сверить не удалось — как у <see cref="SimilarAnswer"/>.</param>
public sealed record LineAnswer(IReadOnlyList<LineHit> Hits, bool More, int Unreadable);

/// <summary>
/// «Похожие» для новой позиции номенклатуры (ТЗ TYPE-8, задача C3, issue #1079) — чистая функция.
///
/// <para><b>Это не нечёткий поиск</b> (решение владельца от 09.10.2026). Правил четыре, и каждое
/// объяснимо человеку одной фразой:</para>
/// <list type="number">
/// <item><b>Тот же ключ</b> — все поля ключа совпали. Создание останавливается.</item>
/// <item><b>Совпало РЕДКОЕ значение поля ключа</b> — так находится тот же артикул. «Редкое» — потому
/// что функция не знает, какое поле артикул, а какое производитель: ключей полей в ядре нет. Значение,
/// общее для сотни позиций («IEK»), позицию не отличает; общее для двух — отличает.</item>
/// <item><b>Так названа другая позиция</b> — её названием при другом остальном ключе либо её
/// альтернативным именем.</item>
/// <item><b>Совпали слова названия</b> — большинство набранных слов есть в названии лежащей или в её
/// альтернативном имени.</item>
/// </list>
///
/// <para>⚠️ <b>Пустое поле ключа — тоже значение.</b> Резолвер ядра позицию с пустым полем ключа не
/// сопоставляет вовсе (ключ не строится), и спроси мы его — позиция без производителя не имела бы
/// двойников никогда. Здесь «кабель, производитель пуст, артикул пуст» равен такому же: вторую
/// позицию с тем же названием и ничем больше человек заводит по ошибке, а не нарочно.</para>
///
/// <para>⚠️ Написанное иначе («3х2,5» и «3*2.5») правила не найдут — окно говорит об этом словами, а
/// не выдаёт пустой ответ за «дублей нет».</para>
/// </summary>
public static class NomenclatureSimilarity
{
    /// <summary>Сколько похожих показываем.</summary>
    public const int Limit = 10;

    /// <summary>Значение поля ключа, общее для большего числа позиций, позицию не отличает.</summary>
    public const int RareLimit = 10;

    private static readonly char[] Breaks = [' ', '(', ')', '[', ']', '«', '»', '"', ';', ':', '/'];

    /// <param name="typed">Набранное: поля ключа в порядке ключа, первое — название.</param>
    public static SimilarAnswer Find(
        IReadOnlyList<(string Title, string? Value)> typed, IReadOnlyList<SimilarRecord> records, int unreadable = 0)
    {
        var asked = typed.Select(t => MatchKeyNormalizer.Normalize(t.Value)).ToList();
        if (asked.Count == 0 || asked[0].Length == 0) return new(null, [], false, unreadable);

        var lying = records.Select(r => (Record: r, Key: KeyOf(r, asked.Count))).ToList();

        // Действующая — раньше архивной: если есть обе, выбирать предлагают ту, что выбирается. Дальше
        // по названию и идентификатору — чтобы из одинаковых ответ был одним и тем же при каждом вопросе.
        var exact = lying
            .Where(x => !x.Record.OwnKey && x.Key.SequenceEqual(asked))
            .OrderBy(x => x.Record.Archived)
            .ThenBy(x => x.Record.Name, StringComparer.Ordinal)
            .ThenBy(x => x.Record.Id)
            .Select(x => x.Record)
            .FirstOrDefault();

        // Сколько позиций делят каждое значение каждого поля — для правила «редкое».
        var shared = Enumerable.Range(0, asked.Count)
            .Select(i => lying.Where(x => x.Key[i].Length > 0)
                .GroupBy(x => x.Key[i]).ToDictionary(g => g.Key, g => g.Count()))
            .ToList();
        var words = Words(asked[0]);

        var hits = new List<(SimilarHit Hit, int Score)>();
        foreach (var (record, key) in lying)
        {
            if (exact is not null && record.Id == exact.Id) continue;

            var same = Enumerable.Range(1, asked.Count - 1)
                .Where(i => asked[i].Length > 0 && key[i] == asked[i] && shared[i][asked[i]] <= RareLimit)
                .ToList();
            if (same.Count > 0)
            {
                hits.Add((new(record, "совпадает " + string.Join(", ",
                    same.Select(i => $"{typed[i].Title.ToLowerInvariant()} «{typed[i].Value!.Trim()}»"))), 400));
                continue;
            }

            if (key[0] == asked[0])
            {
                hits.Add((new(record, key.SequenceEqual(asked)
                    ? "те же поля ключа, но у позиции этого вида есть свои"
                    : $"то же {typed[0].Title.ToLowerInvariant()}, отличается остальное"), 300));
                continue;
            }

            var aliases = record.Aliases.Select(MatchKeyNormalizer.Normalize).Where(a => a.Length > 0).ToList();
            if (aliases.Contains(asked[0]))
            {
                hits.Add((new(record, "так названа альтернативным именем"), 250));
                continue;
            }

            if (words.Count == 0) continue;
            // Лучшее из названия и альтернативных имён: позиция похожа, если похоже любое её имя.
            var found = aliases.Prepend(key[0]).Max(name => Found(words, name));
            // Больше половины слов: одно общее «кабель» из четырёх — не сходство, а раздел справочника.
            if (found * 2 > words.Count || (words.Count == 1 && found == 1))
                hits.Add((new(record, found == words.Count ? "все слова названия" : "слова названия"),
                    100 * found / words.Count));
        }

        var ordered = hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Hit.Record.Archived)
            .ThenBy(h => h.Hit.Record.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(h => h.Hit.Record.Id)
            .Select(h => h.Hit)
            .ToList();

        return new(exact, [.. ordered.Take(Limit)], ordered.Count > Limit, unreadable);
    }

    /// <summary>
    /// Кандидаты для строки бумаги (issue #1513, эпик #1511). Позицию здесь ищут, а не заводят, и
    /// бумага длиннее справочника: «Панель монтажная EKF 71 для щита ЩРН-24 (код mb-71-24)» против
    /// «Панель монтажная 71». Правила по духу те же, что у <see cref="Find"/>, — не нечёткий поиск, каждое
    /// объяснимо фразой. По силе:
    /// <list type="bullet">
    /// <item><b>A, артикул</b> — артикул строки равен РЕДКОМУ значению любого поля ключа, кроме названия
    /// (какое поле артикул, функция не знает — поэтому «редкое», как у <see cref="Find"/>).</item>
    /// <item><b>C, то же наименование</b> — название позиции или её альтернативное имя.</item>
    /// <item><b>D, название целиком</b> — все слова названия позиции (не меньше двух) есть в строке. Это
    /// правило бумаги: «большинство» короткую позицию в длинной строке не находит — 3 слова из 9.</item>
    /// <item><b>E, большинство слов строки</b> — правило <see cref="Find"/>.</item>
    /// </list>
    /// (B — «так выбрали у другого поставщика» — считает модуль счетов: соответствия лежат у него.)
    ///
    /// <para>Одна позиция — одна строка ответа со всеми причинами и уровнем по старшей. Причина по словам
    /// остаётся одна, старшая: «то же наименование» уже значит, что слова совпали, и три фразы об одном
    /// причину не проясняют.</para>
    ///
    /// <para><b>D и E — один уровень</b>, и внутри него решает доля слов строки, найденных в имени
    /// позиции; D лишь выигрывает равенство. Иначе «Кабель ВВГ», который целиком есть почти в любой
    /// строке про кабель, вставал бы выше позиции, совпавшей со строкой слово в слово. Слова сравниваются
    /// одним правилом (<see cref="Matches"/>) — в D, в E, в доле и в подсветке.</para>
    ///
    /// <para>⚠️ <b>Сильный — только единственный.</b> Артикул «00-0029» бывает внутренним кодом 1С
    /// продавца, имя «Кабель ВВГ» — у трёх позиций разного сечения. Нашлось артикулом или тем же
    /// наименованием больше одной ДЕЙСТВУЮЩЕЙ позиции — сильного нет вовсе: Enter выбрал бы одну из них
    /// наугад. Архивные в этот счёт не идут: позицию заменили, старую убрали в архив, — и единственная
    /// действующая остаётся «лучшим совпадением». Сами архивная и позиция подтипа со своими полями ключа
    /// сильными не бывают: первую не выбрать, вторую надо сверить глазами.</para>
    /// </summary>
    /// <param name="titles">Заголовки полей ключа по виду записи, в порядке ключа, — для причины
    /// «совпадает артикул «…»». Вида нет в словаре — «поле ключа».</param>
    public static LineAnswer ForLine(
        LineQuestion line, IReadOnlyList<SimilarRecord> records,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> titles, int unreadable = 0)
    {
        var name = MatchKeyNormalizer.Normalize(line.Name);
        var code = MatchKeyNormalizer.Normalize(line.Code);
        if (name.Length == 0 && code.Length == 0) return new([], false, unreadable);

        var lying = records.Select(r => (Record: r, Key: KeyOf(r, Math.Max(1, r.Identity.Count)))).ToList();
        // Сколько позиций несут значение в каком-либо поле ключа, кроме названия: всех — для «редкого»,
        // действующих — для «единственного». Архивный двойник артикула выбрать нельзя, и единственную
        // действующую позицию он не делает спорной.
        var shared = code.Length == 0 ? [] : SharedValues(lying);
        var sharedLive = code.Length == 0 ? [] : SharedValues(lying.Where(x => !x.Record.Archived));
        var words = Words(name);
        var shown = Shown(line.Name);

        var hits = new List<LineCandidate>();
        foreach (var (record, key) in lying)
        {
            var why = new List<string>();
            int level = 0;
            bool sure = false, whole = false;
            string? field = null;
            var by = key[0];

            var at = code.Length == 0 ? -1 : Array.IndexOf(key, code, 1);
            if (at > 0 && shared[code] <= RareLimit)
            {
                var many = record.Archived ? shared[code] : sharedLive[code];
                field = Title(titles, record.TypeId, at);
                var what = $"{field.ToLowerInvariant()} «{line.Code!.Trim()}»";
                why.Add(many == 1 ? $"совпадает {what}" : $"{what} совпадает у {many} позиций — сверьте");
                level = ByCode;
                sure = many == 1;
            }

            var aliases = record.Aliases.Select(MatchKeyNormalizer.Normalize).Where(a => a.Length > 0).ToList();
            if (name.Length > 0 && key[0] == name)
            {
                why.Add("то же наименование");
                (level, sure) = (Math.Max(level, ByName), true);
            }
            else if (name.Length > 0 && aliases.Contains(name))
            {
                why.Add("так названа альтернативным именем");
                (level, sure, by) = (Math.Max(level, ByName), true, name);
            }
            else if (words.Count > 0)
            {
                // Лучшее из названия и альтернативных имён: позиция подходит, если подходит любое её имя.
                var names = aliases.Prepend(key[0]).Select(n => (Name: n, Own: Words(n))).ToList();
                var full = names.FirstOrDefault(n => n.Own.Count >= 2 && n.Own.All(o => words.Any(w => Matches(w, o))));
                var best = names.MaxBy(n => Counted(words, n.Own));
                var found = Counted(words, best.Own);
                if (full.Name is not null)
                {
                    why.Add("название позиции целиком есть в строке");
                    (level, whole, by) = (Math.Max(level, ByWords), true, full.Name);
                }
                // Больше половины слов: одно общее «кабель» из четырёх — не сходство, а раздел справочника.
                else if (found * 2 > words.Count || (words.Count == 1 && found == 1))
                {
                    why.Add("большинство слов строки");
                    (level, by) = (Math.Max(level, ByWords), best.Name);
                }
            }
            if (level == 0) continue;
            if (record.OwnKey) why.Add("у позиции свои поля ключа — сверьте по карточке");

            // Доля слов строки, найденных в имени, и подсветка — тем же сравнением, что правила: карточка
            // показывает ровно то, по чему подбирали.
            var own = Words(by);
            var share = words.Count == 0 ? 0 : 100 * Counted(words, own) / words.Count;
            var matched = new LineMatched(
                [.. shown.Select(s => new LineWord(s.Text, s.Start, IsWord(s.Lower) && own.Any(o => Matches(s.Lower, o))))],
                field);
            hits.Add(new(record, level, share, whole, sure && !record.Archived && !record.OwnKey, why, matched));
        }

        // Сильный — только если артикулом или тем же наименованием нашлась ОДНА действующая позиция:
        // архивную не выбрать, и спорной находку она не делает.
        var sureOnes = hits.Count(h => h.Level >= ByName && !h.Record.Archived);
        var ordered = hits
            .OrderByDescending(h => h.Level)
            // Внутри «слов» решает доля слов строки, а не правило: «Кабель ВВГ» есть почти в любой строке
            // про кабель, и правилом D он вставал бы выше позиции, совпавшей со строкой целиком.
            .ThenByDescending(h => h.Share)
            .ThenByDescending(h => h.Whole)
            .ThenBy(h => h.Record.Archived)
            .ThenBy(h => h.Record.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(h => h.Record.Id)
            .Select(h => new LineHit(h.Record, h.Why, h.Sure && sureOnes == 1, h.Matched))
            .ToList();

        return new([.. ordered.Take(Limit)], ordered.Count > Limit, unreadable);
    }

    private const int ByCode = 3, ByName = 2, ByWords = 1;

    /// <summary>Кандидат до упорядочения; <c>Sure</c> — сильный, если окажется единственным.</summary>
    private sealed record LineCandidate(
        SimilarRecord Record, int Level, int Share, bool Whole, bool Sure, List<string> Why, LineMatched Matched);

    private static Dictionary<string, int> SharedValues(IEnumerable<(SimilarRecord Record, string[] Key)> lying) =>
        lying.SelectMany(x => x.Key.Skip(1).Where(v => v.Length > 0).Distinct())
            .GroupBy(v => v).ToDictionary(g => g.Key, g => g.Count());

    private static string Title(IReadOnlyDictionary<Guid, IReadOnlyList<string>> titles, Guid type, int at) =>
        titles.TryGetValue(type, out var list) && at < list.Count && !string.IsNullOrWhiteSpace(list[at])
            ? list[at] : "поле ключа";

    /// <summary>
    /// Слово строки и слово позиции — одно слово, в любую сторону: «ВВГ» строки — это «ВВГнг» позиции,
    /// и «ВВГ» позиции — это «ВВГнг» строки. Одно сравнение на правила D и E, долю и подсветку.
    /// </summary>
    private static bool Matches(string word, string own) => Same(word, own) || Same(own, word);

    /// <summary>Сколько слов строки нашлось среди слов имени.</summary>
    private static int Counted(IReadOnlyList<string> words, IReadOnlyList<string> own) =>
        words.Count(w => own.Any(o => Matches(w, o)));

    private static bool IsWord(string token) => token.Length >= 3 || token.Any(char.IsDigit);

    /// <summary>
    /// Слова строки так, как они в ней написаны, с местом в исходной строке: карточка подсвечивает их в
    /// тексте поставщика, со скобками и знаками, а не в списке слов. Разбиение — то же, что у правил.
    /// </summary>
    private static List<(string Text, int Start, string Lower)> Shown(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        // Любой пробельный знак — пробел той же длины: места слов в исходной строке не сдвигаются.
        var spaced = new string([.. text.Select(c => char.IsWhiteSpace(c) ? ' ' : c)]);
        var result = new List<(string, int, string)>();
        var from = 0;
        foreach (var token in Split(spaced))
        {
            var start = spaced.IndexOf(token, from, StringComparison.Ordinal);
            result.Add((token, start, token.ToLowerInvariant()));
            from = start + token.Length;
        }
        return result;
    }

    /// <summary>
    /// Ключ лежащей записи. Названием служит первое поле ключа, а если оно пусто — название самой
    /// записи: запись без данных, названная так же, — тот же двойник.
    /// </summary>
    private static string[] KeyOf(SimilarRecord record, int length)
    {
        var key = new string[length];
        for (var i = 0; i < length; i++)
            key[i] = MatchKeyNormalizer.Normalize(i < record.Identity.Count ? record.Identity[i] : null);
        if (key[0].Length == 0) key[0] = MatchKeyNormalizer.Normalize(record.Name);
        return key;
    }

    /// <summary>
    /// Сколько набранных слов есть в имени — СЛОВАМИ, а не подстрокой. Слово с цифрой обязано совпасть
    /// целиком: «16» внутри «160А», «3х16» и «116» — другие числа, и подстрока вытеснила бы ими
    /// настоящую позицию из десяти показанных. Слово без цифр совпадает и началом: «ВВГ» — это
    /// «ВВГнг».
    /// </summary>
    private static int Found(IReadOnlyList<string> words, string name)
    {
        var lying = Split(name);
        return words.Count(w => lying.Any(l => Same(w, l)));
    }

    /// <summary>Слово с цифрой совпадает только целиком, без цифр — и началом (см. <see cref="Found"/>).</summary>
    private static bool Same(string word, string lying) =>
        word.Any(char.IsDigit) ? lying == word : lying.StartsWith(word, StringComparison.Ordinal);

    /// <summary>
    /// Слова названия. Короткие без цифр («и», «на», «с») не слова: они есть везде. С цифрой — слова
    /// всегда: «2,5» и «16» в названии кабеля значат больше, чем «кабель».
    /// </summary>
    private static List<string> Words(string name) =>
        [.. Split(name).Where(w => w.Length >= 3 || w.Any(char.IsDigit)).Distinct()];

    private static List<string> Split(string name) =>
        [.. name.Split(Breaks, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim(',', '.', '-')).Where(w => w.Length > 0)];
}
