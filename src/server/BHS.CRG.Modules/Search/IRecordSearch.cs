using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Exchange;

namespace BHS.CRG.Modules.Search;

/// <summary>
/// Межмодульный контракт «Поиск записей» (ТЗ AUTH-16.1, CORE-34; shell-05, В-1 — Б): каждый включённый
/// модуль ищет СВОИ записи по строке запроса и отдаёт их карточки по идентификатору. Потребитель — ядро:
/// адрес <c>GET /api/search</c> (палитра, Ctrl K) и «Недавние» Главной (<c>shell-08</c>, В-3 — Б).
///
/// <para><b>Одно объявление «найти» и «показать по id»</b>, а не два контракта: палитра и Главная
/// показывают одну и ту же карточку записи, и разойтись им нечему — название, место и сумма у счёта одни.</para>
///
/// <para><b>Права — у каждой группы, ключом её списка</b> (<see cref="RecordSearchGroup.Requires" />). Ядро
/// не спрашивает группу, на ключ которой у человека права нет, а поставщик сам отвечает
/// <see cref="ContractAnswer{T}.Hidden" /> — второй пояс, а не единственный: забытая проверка у поставщика
/// не открывает записей. Группа без ключа — отказ старта (<c>RecordSearchCatalog</c>).</para>
///
/// <para>Выключенный модуль реализации не регистрирует и просто не отвечает — его группы в ответе нет,
/// остальные отвечают.</para>
/// </summary>
[ModuleContract("Поиск записей", Many = true)]
public interface IRecordSearch
{
    /// <summary>Группы поставщика — объявление, неизменное за время жизни приложения.</summary>
    IReadOnlyList<RecordSearchGroup> Groups { get; }

    /// <summary>
    /// Записи группы <paramref name="group" /> по словам запроса — не больше <see cref="RecordSearchQuery.Take" />
    /// и признак «есть ещё». Поиск ничего не пишет.
    /// </summary>
    Task<ContractAnswer<RecordSearchHits>> SearchAsync(
        string group, RecordSearchQuery query, ModuleReader reader, CancellationToken ct);

    /// <summary>
    /// Карточки записей группы по идентификаторам. Нет записи или она вне прав — её нет в ответе: удалённая
    /// запись и закрытая неотличимы, и число найденных о закрытых ничего не говорит.
    /// </summary>
    Task<ContractAnswer<IReadOnlyList<RecordSearchHit>>> CardsAsync(
        string group, IReadOnlyCollection<Guid> ids, ModuleReader reader, CancellationToken ct);
}

/// <summary>
/// Группа поиска, как её объявляет поставщик.
/// </summary>
/// <param name="Code">Код группы, уникальный на экземпляр: <c>invoices</c>. Им клиент выбирает чип области.</param>
/// <param name="Title">Название группы для человека: «Счета».</param>
/// <param name="RecordType">Код типа записи (<c>СчётНаОплату</c>): по нему клиент знает экран записи, а
/// «Недавние» — владельца записи. Уникален на экземпляр.</param>
/// <param name="Requires">Ключ доступа — то же право, что у списка записей группы (<c>costs.invoice.read</c>).</param>
/// <param name="Archive">Что группа делает с архивными записями (ТЗ CORE-34.4): решение объявлено, а не
/// подразумевается.</param>
public sealed record RecordSearchGroup(
    string Code, string Title, string RecordType, string Requires, RecordSearchArchive Archive);

/// <summary>
/// Архив в поиске (ТЗ CORE-34.4, правило <c>purpose=choice</c>, #1185): поиск — это выбор, и архивная запись
/// в нём не предлагается.
/// </summary>
public enum RecordSearchArchive
{
    /// <summary>У записей группы архивного состояния нет (счёт, стройка, набор данных).</summary>
    None,

    /// <summary>Архив есть, и поставщик читает записи назначением «выбор» (<c>RecordsFor.Choice</c>).</summary>
    Choice,
}

/// <summary>
/// Запрос к группе: слова запроса (уже без регистра и с «ё» → «е») и предел. Запись подходит, если каждое
/// слово найдено хоть в одном её поле — в любом порядке, как в палитре на клиенте.
/// </summary>
public sealed record RecordSearchQuery(IReadOnlyList<string> Words, int Take);

/// <summary>Найденные записи группы и признак, что подходящих больше, чем отдано.</summary>
public sealed record RecordSearchHits(IReadOnlyList<RecordSearchHit> Items, bool More);

/// <summary>
/// Карточка записи (shell-05, М-4): что палитра и «Недавние» показывают строкой.
/// </summary>
/// <param name="Id">Идентификатор записи — им клиент открывает её экран.</param>
/// <param name="Title">Подпись: «№ 418 · ООО „КабельТорг“», название стройки, набора.</param>
/// <param name="Detail">Пояснение: назначение счёта, внешний код стройки.</param>
/// <param name="Place">Где запись: стройка набора данных.</param>
/// <param name="Amount">Сумма — у счёта; форматирует клиент.</param>
/// <param name="Level">Уровень записи — у той, чей экран зависит от уровня (набор данных).</param>
public sealed record RecordSearchHit(
    Guid Id, string Title, string? Detail = null, string? Place = null, decimal? Amount = null,
    RecordSearchLevel? Level = null);

/// <summary>
/// Где лежит запись, у которой экран свой на каждом уровне (shell-05, решение владельца 12.11.2026 — «А»):
/// набор данных открывается на экране своего уровня, и адрес его клиент строит отсюда, а не новым запросом.
/// </summary>
/// <param name="Scope">Уровень: <c>System</c>, <c>Construction</c>, <c>Section</c>, <c>Set</c>.</param>
/// <param name="ScopeId">Запись уровня — стройка, раздел или комплект; у системного уровня нет.</param>
/// <param name="ConstructionId">Стройка уровня — с неё начинается адрес раздела и комплекта.</param>
public sealed record RecordSearchLevel(string Scope, Guid? ScopeId = null, Guid? ConstructionId = null);

/// <summary>Сопоставление строки со словами запроса — одно правило у поставщиков и ядра.</summary>
public static class RecordSearchText
{
    /// <summary>
    /// Слова запроса: без регистра, «ё» как «е», знаки по краям слова сняты, пустые выброшены. Знак по краю
    /// — не часть того, что ищут: «№ 418» и ««КабельТорг»» набраны с карточки, а в полях записи хранится
    /// «418» и «КабельТорг». Знак внутри слова остаётся: «РОЛ-418» — номер целиком.
    /// </summary>
    public static IReadOnlyList<string> Words(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? []
            : [.. Normalize(query).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Trim(EdgeMarks(w)))
                .Where(w => w.Length > 0)
                .Distinct(StringComparer.Ordinal)];

    /// <summary>Знаки по краям слова — всё, что не буква и не цифра.</summary>
    private static char[] EdgeMarks(string word) => [.. word.Where(c => !char.IsLetterOrDigit(c)).Distinct()];

    /// <summary>Строка к сравнению: нижний регистр, «ё» как «е».</summary>
    public static string Normalize(string text) => text.ToLowerInvariant().Replace('ё', 'е');

    /// <summary>
    /// Образец для <c>ILIKE</c>: подстрока слова, со знаками <c>%</c>, <c>_</c> и <c>\</c> экранированными —
    /// иначе «50%» искал бы всё, что начинается с «50».
    /// </summary>
    public static string LikePattern(string word) =>
        "%" + word.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";

    /// <summary>Есть ли слово в одном из полей (поля сравниваются нормализованными).</summary>
    public static bool AnyContains(string word, params string?[] fields) =>
        fields.Any(f => f is not null && Normalize(f).Contains(word, StringComparison.Ordinal));
}
