using System.Text;
using System.Text.RegularExpressions;

namespace BHS.CRG.Application.Generation;

/// <summary>Куда переезжает блок: код его типа и новое имя (совпадает со старым, если не срезали).</summary>
public sealed record BlockRef(string TypeCode, string OldName, string NewName);

/// <param name="Text">Новый текст (равен исходному, если менять было нечего).</param>
/// <param name="Calls">Сколько вызовов переписано.</param>
/// <param name="Paths">Сколько путей переведено на запись от корня.</param>
/// <param name="Ambiguous">Имена блоков, встреченные НЕ в позиции вызова: переписывать их наугад
/// нельзя — это может быть передача функции значением, текст документа или чужой идентификатор.</param>
public sealed record RewriteResult(
    string Text, int Calls, int Paths, IReadOnlyList<string> Ambiguous);

/// <summary>
/// Переписывает тексты под адресацию <c>Код.Имя</c> (issue #773): вызовы блоков и — заодно — пути к
/// файлам, съехавшие после переезда блоков в подпапку (#772).
///
/// <para><b>Замены применяются к ОРИГИНАЛУ по позициям, найденным в маске.</b> Маска
/// (<see cref="TypstTextMask"/>) той же длины, поэтому ни один символ вне найденных вхождений не
/// двигается: комментарий, строка и raw-блок остаются ровно такими, как их написал человек. Это и
/// есть главное требование к переписчику — он правит пользовательский текст, и «почти правильно»
/// здесь означает испорченный шаблон.</para>
///
/// <para><b>Карта имён строится ОДИН раз до любой правки.</b> До миграции имена блоков глобально
/// уникальны, после — нет: <c>full</c> будет и у «Адреса», и у «Подписанта». Значит однозначно
/// разрешить старое имя можно только по домиграционному снимку, а повторный прогон по уже
/// переписанному тексту недопустим. От него защищает и сам поиск: имя после точки не считается
/// вызовом, поэтому <c>Организация.full</c> второй раз не тронется.</para>
/// </summary>
public static class TypstCallRewriter
{
    /// <summary>
    /// </summary>
    /// <param name="fixPaths">Чинить ли относительные пути (хвост #772). Только для текстов блоков.</param>
    /// <param name="ownTypeCode">Код типа, ЧЬИ блоки пишутся в этом тексте (для текста блока), либо
    /// null для шаблона. Вызов блока своего типа остаётся без префикса — модуль общий, — и получает
    /// только новое имя; вызов чужого адресуется через код.</param>
    public static RewriteResult Rewrite(
        string text, IReadOnlyDictionary<string, BlockRef> byOldName, string? ownTypeCode = null,
        bool fixPaths = false)
    {
        if (string.IsNullOrEmpty(text)) return new(text, 0, 0, []);

        var edits = new List<(int Start, int Length, string Replacement)>();
        var ambiguous = new List<string>();

        var forCalls = TypstTextMask.Mask(text, TypstTextMask.Keep.CodeOnly);
        foreach (var (oldName, target) in byOldName)
        {
            foreach (Match m in Regex.Matches(forCalls, CallPattern(oldName)))
                edits.Add((m.Index, oldName.Length, Address(target, ownTypeCode)));

            // Упоминание вне позиции вызова — не переписываем, а называем: сегодня таких ноль, и
            // правило бесплатно; сработает оно только на чём-то новом, где догадка была бы опасна.
            if (Regex.IsMatch(forCalls, MentionPattern(oldName)))
                ambiguous.Add(oldName);
        }

        var calls = edits.Count;

        // Пути правим ТОЛЬКО в текстах блоков: это они переехали в подпапку, а шаблон как лежал
        // в корне компиляции, так и лежит — там «userlib.typ» и так резолвится. Трогать его значило
        // бы менять чужой текст без нужды и заводить новую версию шаблона ради пустой правки.
        //
        // Маска — «только строки»: путь вне литерала это проза документа, и «смета.pdf» в тексте
        // превратилась бы в «/смета.pdf» прямо в PDF.
        if (fixPaths)
            foreach (Match m in RelativePath.Matches(TypstTextMask.Mask(text, TypstTextMask.Keep.StringsOnly)))
                edits.Add((m.Groups[1].Index, 0, "/"));

        var paths = edits.Count - calls;
        if (edits.Count == 0) return new(text, 0, 0, ambiguous);

        var sb = new StringBuilder(text);
        foreach (var e in edits.OrderByDescending(e => e.Start))
        {
            sb.Remove(e.Start, e.Length);
            sb.Insert(e.Start, e.Replacement);
        }
        return new(sb.ToString(), calls, paths, ambiguous);
    }

    /// <summary>Как записать вызов: свой блок — по имени, чужой — через код типа.</summary>
    private static string Address(BlockRef target, string? ownTypeCode) =>
        string.Equals(target.TypeCode, ownTypeCode, StringComparison.Ordinal)
            ? target.NewName
            : $"{target.TypeCode}.{target.NewName}";

    /// <summary>Вызов: имя на границе идентификатора, за ним скобка. Точка в lookbehind — защита от
    /// повторного прогона: <c>Код.имя(</c> уже переписан.</summary>
    private static string CallPattern(string name)
        => $@"(?<![\w\-.]){Regex.Escape(name)}\s*(?=\()";

    private static string MentionPattern(string name)
        => $@"(?<![\w\-.]){Regex.Escape(name)}(?![\w\-])(?!\s*\()";

    /// <summary>Путь к файлу без ведущей «/» и без «../» — тот, что сломался с переездом в подпапку.
    /// Группа 1 — начало пути: вставляем «/» перед ней, не трогая остальное.</summary>
    private static readonly Regex RelativePath = new(
        @"""(?!/|\.\./|[a-z]+://)([^""\n]+\.(?:typ|json|csv|toml|yaml|yml|xml|png|jpg|jpeg|gif|svg|pdf|bib))""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
