using System.Globalization;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Endpoints;
using BHS.CRG.Modules.Tables;

namespace BHS.CRG.Modules.Costs.Tables;

/// <summary>
/// Объекты разноски счёта и его ДОЛЯ на названные отбором объекты (ТЗ CORE-33, COST-20.1; задача G1c,
/// issue #1090).
///
/// <para><b>Долю считает та же арифметика, что и счёт</b> (<see cref="AllocationMath" />), в памяти, а
/// не пропорцией в запросе; читает её общий читатель денег счетов (<see cref="InvoiceMoney" />). Пропорция в базе разошлась бы с ней на копейки округления и на расхождение
/// с суммой к оплате, которые уходят в последнюю часть (ТЗ COST-13), — и итог реестра по стройке не
/// сошёлся бы со счётом, открытым рядом. Цена — строки и части счетов отбора читаются целиком; у
/// итога это ВЕСЬ отбор, а не страница. Отбор по объекту при этом уже сузил счета до одной стройки.
/// ⚠️ Отбор по учётному периоду (<see cref="InvoicePeriods" />) платит ту же цену и так не сужает:
/// «период содержит 2026» — оплаченные счета года целиком на каждое чтение с итогом по «Сумме».
/// Цена известна и принята до «Затрат по стройке» (G5, issue #1098), где деньги месяца понадобятся
/// свёрткой.</para>
///
/// <para><b>Раздел называется вместе со стройкой</b> — «Комарова 36 / 4 эт.» (решение владельца
/// 05.10.2026; задача G5b, issue #1198). Разделы зовут «4 эт.» и «ливнёвка» на каждой стройке, а отбор
/// реестра идёт по названию: одинокое «4 эт.» находило бы разделы всех строек разом, и ссылка отчёта
/// показывала бы больше, чем его строка. Доля на стройку целиком — тоже значение, «Комарова 36 / без
/// раздела»: пустую клетку нечем отобрать, и строке «без раздела» в отчёте некуда было бы вести.</para>
/// </summary>
internal sealed class InvoiceShares(
    IReadOnlyDictionary<Guid, string> labels, IReadOnlyDictionary<Guid, string> sections)
{
    /// <summary>Доли и разделы по местам разноски — одним способом на реестр и на «Затраты по стройке»:
    /// ссылка отчёта несёт название, и назови он раздел по-своему, реестр под ней не нашёл бы ничего.</summary>
    public static InvoiceShares Of(AllocationPlaces known) => new(
        known.Sites.Select(s => (s.Id, s.Name)).Concat(known.Articles.Select(a => (a.Id, a.Name)))
            .ToDictionary(o => o.Id, o => o.Name),
        known.Sites.SelectMany(s => s.Sections.Select(x => (x.Id, Name: SectionLabel(s.Name, x.Name)))
                .Append((s.Id, Name: SectionLabel(s.Name, NoSection))))
            // Ключи из двух справочников ядра; совпасть им нечем, но словарь не должен ронять реестр.
            .GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().Name));

    /// <summary>Названия объектов по ссылкам — стройки и статьи вне строек одним списком.</summary>
    public IReadOnlyDictionary<Guid, string> Labels => labels;

    /// <summary>
    /// Названия разделов по ключу раздела доли (<see cref="SectionKey" />): раздел — своим
    /// идентификатором, «без раздела» — идентификатором стройки.
    /// </summary>
    public IReadOnlyDictionary<Guid, string> Sections => sections;

    /// <summary>Доля на стройку целиком — раздел не назван.</summary>
    public const string NoSection = "без раздела";

    /// <summary>
    /// Как назван раздел, которого больше нет. ⚠️ Так же названа и доля БЕЗ раздела на удалённой
    /// стройке: вместе со стройкой ушли её разделы, и назвать «без раздела чего» уже нечем.
    /// </summary>
    public const string LostSection = "раздел удалён";

    /// <summary>«Комарова 36 / 4 эт.» — так раздел зовут колонка реестра, её отбор и ссылка отчёта.</summary>
    public static string SectionLabel(string site, string section) => $"{site} / {section}";

    /// <summary>
    /// Ключ раздела доли — он же выражение колонки в запросе (<c>InvoiceTableRows.Sql</c>): раздел либо,
    /// если доля на стройку целиком, сама стройка. У доли на статью вне строек раздела нет.
    /// </summary>
    public static Guid? SectionKey(InvoiceAllocation part) =>
        part.ConstructionId is null ? null : part.SectionId ?? part.ConstructionId;

    /// <summary>Раздел доли так, как его зовёт колонка «Раздел»; null — доля на статью вне строек.</summary>
    public string? Section(InvoiceAllocation part) =>
        SectionKey(part) is { } key ? sections.GetValueOrDefault(key, LostSection) : null;

    /// <summary>Как названа цель, которой больше нет, — стройку удалили в ядре, статью — в справочнике.</summary>
    public const string Lost = "объект удалён";

    /// <summary>Порядок объектов по названию — один на колонку «Объект» и на расшифровку строки.</summary>
    internal static readonly StringComparer ByName = StringComparer.Create(CultureInfo.GetCultureInfo("ru-RU"), true);

    /// <summary>Объект части: стройка или статья вне строек — ровно одно из двух (держит база).</summary>
    public string Label(InvoiceAllocation part) =>
        (part.ConstructionId ?? part.ArticleId) is { } key && labels.TryGetValue(key, out var label) ? label : Lost;

    /// <summary>Объекты счетов страницы — названиями по алфавиту, каждый по разу.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<string>> Objects(IEnumerable<InvoiceAllocation> parts) =>
        Listed(parts, Label);

    /// <summary>Разделы счетов страницы — так же; доли на статьи вне строек в перечень не идут.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<string>> SectionsOf(IEnumerable<InvoiceAllocation> parts) =>
        Listed(parts, Section);

    private static IReadOnlyDictionary<Guid, IReadOnlyList<string>> Listed(
        IEnumerable<InvoiceAllocation> parts, Func<InvoiceAllocation, string?> label) =>
        parts.GroupBy(p => p.InvoiceId).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<string>)[.. g.Select(label).OfType<string>().Distinct(StringComparer.Ordinal).Order(ByName)]);

    /// <summary>
    /// Подпись колонки суммы под отбором по объекту: «доля: Комарова 36». Объекты — те, чьё название
    /// подошло под условия отбора; длинный перечень заменяется числом — подпись стоит в заголовке.
    /// </summary>
    public string Note(IReadOnlyList<TableFilterCondition> naming) =>
        Note(naming, labels.Values.Append(Lost), "доля", "объектов");

    /// <summary>То же под отбором по разделу: «раздел: Комарова 36 / 4 эт.».</summary>
    public string SectionNote(IReadOnlyList<TableFilterCondition> naming) =>
        Note(naming, sections.Values.Append(LostSection), "раздел", "разделов");

    private static string Note(
        IReadOnlyList<TableFilterCondition> naming, IEnumerable<string> known, string what, string many)
    {
        var named = known.Distinct(StringComparer.Ordinal)
            .Where(label => naming.Any(c => c.Matches(label)))
            .Order(ByName).ToList();

        return named.Count switch
        {
            0 => $"{what}: названных {many} нет",
            <= 3 => $"{what}: {string.Join("; ", named)}",
            _ => $"{what}: {many} — {named.Count}",
        };
    }

    /// <summary>Итог по долям — тот же, что считал бы запрос по числовой колонке.</summary>
    public static TableTotal Total(IEnumerable<decimal?> shares)
    {
        var known = shares.Where(s => s is not null).Select(s => s!.Value).ToList();
        return known.Count == 0 ? new(0, 0) : new(known.Count, 0, known.Sum(), known.Min(), known.Max());
    }
}
