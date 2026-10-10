using BHS.CRG.Application.QualityDocs;

namespace BHS.CRG.Application.Documents;

/// <summary>
/// Лежащая позиция глазами сверки: название и значения полей ключа в порядке ключа.
/// </summary>
/// <param name="Identity">Значения полей ключа; <c>null</c> — поля у записи нет или оно пустое.</param>
public sealed record SimilarRecord(
    Guid Id, Guid TypeId, string? Name, bool Archived, IReadOnlyList<string?> Identity);

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
/// «Похожие» для новой позиции номенклатуры (ТЗ TYPE-8, задача C3, issue #1079) — чистая функция.
///
/// <para><b>Это не нечёткий поиск</b> (решение владельца от 09.10.2026). Правил три, и каждое
/// объяснимо человеку одной фразой:</para>
/// <list type="number">
/// <item><b>Тот же ключ</b> — все поля ключа совпали. Создание останавливается.</item>
/// <item><b>Совпало РЕДКОЕ значение поля ключа</b> — так находится тот же артикул. «Редкое» — потому
/// что функция не знает, какое поле артикул, а какое производитель: ключей полей в ядре нет. Значение,
/// общее для сотни позиций («IEK»), позицию не отличает; общее для двух — отличает.</item>
/// <item><b>Совпали слова названия</b> — большинство набранных слов есть в названии лежащей.</item>
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

    /// <param name="typed">Набранное: поля ключа в порядке ключа, первое — название.</param>
    public static SimilarAnswer Find(
        IReadOnlyList<(string Title, string? Value)> typed, IReadOnlyList<SimilarRecord> records, int unreadable = 0)
    {
        var asked = typed.Select(t => MatchKeyNormalizer.Normalize(t.Value)).ToList();
        if (asked.Count == 0 || asked[0].Length == 0) return new(null, [], false, unreadable);

        var lying = records.Select(r => (Record: r, Key: KeyOf(r, asked.Count))).ToList();

        // Действующая — раньше архивной: если есть обе, выбирать предлагают ту, что выбирается.
        var exact = lying
            .Where(x => x.Key.SequenceEqual(asked))
            .OrderBy(x => x.Record.Archived)
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
                    same.Select(i => $"{typed[i].Title.ToLowerInvariant()} «{typed[i].Value!.Trim()}»"))), 300));
                continue;
            }

            if (key[0] == asked[0])
            {
                hits.Add((new(record, $"то же {typed[0].Title.ToLowerInvariant()}, отличается остальное"), 200));
                continue;
            }

            if (words.Count == 0) continue;
            var found = words.Count(w => key[0].Contains(w, StringComparison.Ordinal));
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
    /// Слова названия. Короткие без цифр («и», «на», «с») не слова: они есть везде. С цифрой — слова
    /// всегда: «2,5» и «16» в названии кабеля значат больше, чем «кабель».
    /// </summary>
    private static List<string> Words(string name) =>
        [.. name.Split([' ', '(', ')', '[', ']', '«', '»', '"', ';', ':', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim(',', '.', '-'))
            .Where(w => w.Length >= 3 || w.Any(char.IsDigit))
            .Distinct()];
}
