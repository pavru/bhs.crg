using BHS.CRG.Application.Common;

namespace BHS.CRG.Application.Objects;

/// <summary>
/// Кто ВНЕ ядра держит записи ядра — данные модулей (ТЗ CORE-34.1, CORE-34.2; задача G2,
/// issue #1094).
///
/// <para>Индекс ссылок ядра (<see cref="IReferenceIndex" />) видит только свои таблицы. Позиция
/// номенклатуры, стоящая в строке счёта, для него свободна — и удалялась, оставляя счёт со ссылкой
/// в пустоту (issue #1168). Этот порт — второй вопрос того же рода, и задаёт его КАЖДЫЙ путь
/// удаления записи ядра: полноту стережёт перепись <c>RecordDeletionInventoryTests</c>.</para>
///
/// <para>Ответ одинаков при включённом и выключенном модуле: данные выключенного на месте и держат
/// так же (ТЗ AUTH-19). Как держатели находятся и почему не по объявлениям модулей — см. реализацию.</para>
///
/// <para>Заменил <c>IWorkPlanItemReferrer</c> (issue #964): тот спрашивал только о позициях перечня и
/// только у включённых модулей — выключенный своих служб не регистрирует, и о его ссылках не узнавал
/// никто.</para>
/// </summary>
public interface IRecordHolders
{
    /// <summary>
    /// Кто держит эти записи — словами, для отказа человеку. Спрашивается о ГРУППЕ: удаление уровня
    /// уносит поддерево разом.
    /// </summary>
    Task<RecordHoldings> FindAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>
    /// Какие из записей держат — без слов. Для пути без человека (уборка сирот): ему нужно множество,
    /// а названия документов стоили бы запроса на каждую держащую колонку впустую.
    /// </summary>
    Task<HeldRecords> HeldAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
}

/// <param name="Ids">Какие из спрошенных записей держат.</param>
/// <param name="Verified">
/// Удалось ли проверить. <c>false</c> — данные вне ядра не прочитаны, и <paramref name="Ids" /> пуст
/// НЕ потому, что никто не держит: трогать нельзя ни одну из спрошенных записей. Отдельным полем, а
/// не исключением, потому что сухой прогон уборки обязан ответить отчётом, а не отказом.
/// </param>
public sealed record HeldRecords(IReadOnlySet<Guid> Ids, bool Verified);

/// <summary>Чьи данные держат запись — от этого зависит, есть ли где убрать ссылку.</summary>
public enum HolderState
{
    /// <summary>Модуль включён: ссылку убирают в нём обычным путём.</summary>
    Enabled,

    /// <summary>Модуль в сборке есть, но выключен: его данные на месте, а открыть их негде.</summary>
    Disabled,

    /// <summary>Схема, которую не назвал ни один модуль сборки: модуль сняли или ещё не вернули.</summary>
    Absent,
}

/// <summary>
/// Один держатель — колонка вне ядра (issue #1187). Рядом со словами отказа лежит то, по чему
/// решают: чьи это данные и сколько строк. Решать по словам нельзя — они составлены для человека и
/// зависят от его прав.
/// </summary>
/// <param name="Owner">Чьи данные, словами: «„Счета и накладные“ (модуль выключен)».</param>
/// <param name="What">Что держит: «строки счетов»; у необъявленной колонки — «записи».</param>
/// <param name="Rows">Сколько строк держат.</param>
/// <param name="Traceable">
/// Найдёт ли эти ссылки обратный опрос после удаления записи. Он идёт по объявлениям модуля, и
/// ссылку в необъявленной колонке или в схеме без модуля не покажет никто и никогда.
/// </param>
/// <param name="Address">Адрес колонки — для журнала; человеку его показывает <paramref name="Line" />,
/// и только администратору.</param>
/// <param name="Documents">Названия документов-держателей — если спрашивающему их видеть можно.</param>
/// <param name="Line">Строка отказа, составленная для того, кто спрашивает.</param>
public sealed record RecordHolder(
    HolderState State, string Owner, string What, int Rows, bool Traceable,
    string Address, string? Documents, string Line);

/// <summary>
/// Запись держат только данные, в которых ссылку убрать негде, — её можно удалить принудительно
/// (issue #1187). <paramref name="References" /> — число, которое человек видит, вводит и которое
/// уходит в журнал: все три берутся отсюда, из одного ответа на один вопрос.
/// </summary>
public sealed record DormantRelease(int References, IReadOnlyList<RecordHolder> Holders)
{
    /// <summary>Сколько из ссылок после удаления не покажет никто.</summary>
    public int Untraceable => Holders.Where(h => !h.Traceable).Sum(h => h.Rows);
}

/// <summary>
/// Отказ «запись держат данные модулей» — с самим ответом, а не только словами: адрес отдаёт экрану
/// поле «можно удалить принудительно», и разбирать для этого фразу он не должен.
/// </summary>
public sealed class RecordHeldException(string message, RecordHoldings holdings) : ConflictException(message)
{
    public RecordHoldings Holdings { get; } = holdings;
}

/// <summary>
/// Ответ на вопрос «кто держит». У него три исхода, а не два: держат, не держат — и «проверить не
/// удалось». Третий обязан читаться как отказ: непрочитанные данные могли держать, и принять его за
/// «никто не держит» значило бы удалить занятое.
/// </summary>
public sealed class RecordHoldings
{
    /// <param name="lines">
    /// Держатели строками «что — сколько», готовыми для отказа. Составлены ДЛЯ ТОГО, КТО СПРАШИВАЕТ:
    /// названия документов — только при праве чтения модуля, адрес таблицы — только администратору.
    /// </param>
    public RecordHoldings(IReadOnlyList<string> lines) => Lines = lines;

    /// <summary>Держатели со структурой: строки отказа берутся из них же.</summary>
    public RecordHoldings(IReadOnlyList<RecordHolder> holders) : this([.. holders.Select(h => h.Line)]) =>
        Holders = holders;

    public static readonly RecordHoldings None = new(Array.Empty<string>());

    /// <summary>
    /// Проверить не удалось: данные вне ядра не прочитаны. <paramref name="why" /> — текст для того,
    /// кто спрашивает; о действии он молчит — тем же ответом пользуется и показ «чем занят тип», где
    /// никто ничего не удаляет.
    /// </summary>
    public static RecordHoldings Unverified(string why) => new([why]) { IsUnverified = true };

    public bool IsUnverified { get; private init; }

    /// <summary>Строки держателей; у непроверенного ответа — одна, с объяснением.</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>Держатели по одному; у непроверенного ответа их нет — и это не «никто не держит».</summary>
    public IReadOnlyList<RecordHolder> Holders { get; } = [];

    /// <summary>Держат — или проверить не удалось: удалять нельзя в обоих случаях.</summary>
    public bool Any => Lines.Count > 0;

    /// <summary>
    /// Можно ли отпустить запись принудительно: держат, проверено, и ни один держатель не включён.
    /// <c>null</c> во всех остальных случаях — в том числе когда никто не держит: тогда запись
    /// удаляют обычным путём, и вторым обычным удалением этот выход быть не должен.
    /// </summary>
    public DormantRelease? Release =>
        !IsUnverified && Holders.Count > 0 && Holders.All(h => h.State != HolderState.Enabled)
            ? new DormantRelease(Holders.Sum(h => h.Rows), Holders)
            : null;

    /// <summary>
    /// Отказ, если запись держат. <paramref name="what" /> — что удаляют, в винительном падеже:
    /// «запись», «стройку», «тип».
    ///
    /// <para>Число в отказе обязательно: «на запись ссылаются» не говорит человеку, сколько работы его
    /// ждёт. Выход назван один — убрать ссылки: другого сегодня нет (архив — issue #1185).</para>
    /// </summary>
    public void EnsureNone(string what)
    {
        if (IsUnverified) throw new ConflictException($"Удаление отменено. {Lines[0]}");
        if (!Any) return;

        throw Held(what);
    }

    /// <summary>
    /// Принудительное удаление (issue #1187): пропускает, только если запись держат ИСКЛЮЧИТЕЛЬНО
    /// данные, в которых ссылку убрать негде, и человек назвал их число. ⚠️ Единственное место, где
    /// это решается: право на принудительное удаление не даёт удалить то, что держит включённый
    /// модуль, именно потому, что мимо этого метода принудительного пути нет.
    /// </summary>
    public DormantRelease EnsureOnlyDormant(string what, int confirmed)
    {
        if (IsUnverified) throw new ConflictException($"Удаление отменено. {Lines[0]}");
        if (!Any)
            throw new ConflictException(
                $"На {what} никто не ссылается — удалите обычным путём: принудительное удаление " +
                "нужно только там, где ссылку убрать негде.");
        if (Release is not { } release) throw Held(what);
        if (release.References != confirmed)
            throw new RecordHeldException(
                $"Число ссылок не совпало: сейчас их {release.References}, а подтверждено {confirmed}. " +
                "Ничего не удалено. Сверьте число и подтвердите заново.", this);
        return release;
    }

    private RecordHeldException Held(string what) =>
        new($"Нельзя удалить {what}: на это ссылаются данные модулей — {string.Join("; ", Lines)}. " +
            "Удаление оставило бы эти ссылки вести в пустоту. " +
            (Release is null
                ? "Уберите их в модуле (если он выключен — включите его), после этого удаление пройдёт."
                : "Убрать их можно только в модуле, а он выключен или снят."),
            this);
}
