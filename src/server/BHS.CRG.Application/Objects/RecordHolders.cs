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

    public static readonly RecordHoldings None = new([]);

    /// <summary>
    /// Проверить не удалось: данные вне ядра не прочитаны. <paramref name="why" /> — текст для того,
    /// кто спрашивает; о действии он молчит — тем же ответом пользуется и показ «чем занят тип», где
    /// никто ничего не удаляет.
    /// </summary>
    public static RecordHoldings Unverified(string why) => new([why]) { IsUnverified = true };

    public bool IsUnverified { get; private init; }

    /// <summary>Строки держателей; у непроверенного ответа — одна, с объяснением.</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>Держат — или проверить не удалось: удалять нельзя в обоих случаях.</summary>
    public bool Any => Lines.Count > 0;

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

        throw new ConflictException(
            $"Нельзя удалить {what}: на это ссылаются данные модулей — {string.Join("; ", Lines)}. " +
            "Удаление оставило бы эти ссылки вести в пустоту. Уберите их в модуле (если он выключен — " +
            "включите его), после этого удаление пройдёт.");
    }
}
