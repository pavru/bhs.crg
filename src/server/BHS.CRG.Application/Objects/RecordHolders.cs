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
    /// Кто держит эти записи. Спрашивается о ГРУППЕ: удаление уровня уносит поддерево разом.
    /// </summary>
    Task<RecordHoldings> FindAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
}

/// <summary>
/// Ответ на вопрос «кто держит». У него три исхода, а не два: держат, не держат — и «проверить не
/// удалось». Третий обязан читаться как отказ на любом пути: непрочитанные данные могли держать, и
/// принять его за «никто не держит» значило бы удалить занятое.
/// </summary>
public sealed class RecordHoldings
{
    private readonly IReadOnlySet<Guid> held;
    private readonly string? unverified;

    /// <param name="held">Какие из спрошенных записей держат.</param>
    /// <param name="lines">
    /// Держатели строками «что — сколько», готовыми для отказа. Составлены ДЛЯ ТОГО, КТО СПРАШИВАЕТ:
    /// названия документов — только при праве чтения модуля, адрес таблицы — только администратору.
    /// </param>
    public RecordHoldings(IReadOnlySet<Guid> held, IReadOnlyList<string> lines)
    {
        this.held = held;
        Lines = lines;
    }

    private RecordHoldings(string unverified)
    {
        held = new HashSet<Guid>();
        Lines = [unverified];
        this.unverified = unverified;
    }

    public static readonly RecordHoldings None = new(new HashSet<Guid>(), []);

    /// <summary>
    /// Проверить не удалось: данные вне ядра не прочитаны. <paramref name="why" /> — текст для того,
    /// кто спрашивает.
    /// </summary>
    public static RecordHoldings Unverified(string why) => new(why);

    public IReadOnlyList<string> Lines { get; }

    /// <summary>Держат — или проверить не удалось: удалять нельзя в обоих случаях.</summary>
    public bool Any => Lines.Count > 0;

    /// <summary>
    /// Какие записи держат. Непроверенный ответ здесь ОТКАЗЫВАЕТ, а не отдаёт пустое множество: этим
    /// свойством пользуется путь без человека (уборка сирот), и пустое множество он прочёл бы как
    /// «свободны все».
    /// </summary>
    public IReadOnlySet<Guid> Held => unverified is null ? held : throw new ConflictException(unverified);

    /// <summary>
    /// Отказ, если запись держат. <paramref name="what" /> — что удаляют, в винительном падеже:
    /// «запись», «стройку», «тип».
    ///
    /// <para>Число в отказе обязательно: «на запись ссылаются» не говорит человеку, сколько работы его
    /// ждёт. Выход назван один — убрать ссылки: другого сегодня нет (архив — issue #1185).</para>
    /// </summary>
    public void EnsureNone(string what)
    {
        if (unverified is not null) throw new ConflictException(unverified);
        if (!Any) return;

        throw new ConflictException(
            $"Нельзя удалить {what}: на это ссылаются данные модулей — {string.Join("; ", Lines)}. " +
            "Удаление оставило бы эти ссылки вести в пустоту. Уберите их в модуле (если он выключен — " +
            "включите его), после этого удаление пройдёт.");
    }
}
