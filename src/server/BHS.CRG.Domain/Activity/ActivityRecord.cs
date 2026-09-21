namespace BHS.CRG.Domain.Activity;

/// <summary>
/// Запись журнала действий (ТЗ CORE-25, CORE-28): кто, когда, что сделал и что было до.
///
/// Журнал отвечает на вопрос, который задают уже после того, как что-то пошло не так: «кто выдал
/// это право» и «когда у типа пропало поле». Ответить на него по данным нельзя — данные показывают
/// нынешнее состояние и не помнят, как до него дошли.
///
/// ⚠️ Запись НЕИЗМЕНЯЕМА и не удаляется: у свойств нет ни одного открытого сеттера, а правку и
/// удаление отвергает <c>AppDbContext.SaveChanges</c>. Журнал, в котором можно поправить строку,
/// перестаёт быть свидетельством, продолжая выглядеть им.
///
/// ⚠️ Не наследует <c>Entity</c> НАРОЧНО: у той есть <c>UpdatedAt</c> и <c>TouchUpdatedAt()</c> —
/// время последней правки у того, что не правят. Колонка, которой нечего показывать, рано или
/// поздно чем-нибудь заполняется.
/// </summary>
public class ActivityRecord
{
    /// <summary>
    /// Ширины колонок-снимков. Объявлены ЗДЕСЬ, а не только в настройке EF, потому что обрезать
    /// приходится до вставки, а место обрезки и объявленная ширина обязаны совпадать: разойдясь,
    /// они дают отказ базы 22001 — и не при записи в журнал, а «внутри» удавшегося действия.
    /// Поэтому число здесь ОДНО: настройка EF берёт его отсюда же, а не повторяет литералом.
    /// </summary>
    public const int ActorNameMax = 256;

    /// <inheritdoc cref="ActorNameMax" />
    public const int TargetLabelMax = 512;

    // ReSharper disable once UnusedMember.Local — конструктор для EF.
    private ActivityRecord() { }

    public Guid Id { get; private set; }

    /// <summary>Когда действие произошло. Время сервера в UTC; показывается в поясе браузера.</summary>
    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>Код действия из каталога <c>ActivityActions</c>: <c>core.user.role.changed</c>.</summary>
    public string Action { get; private set; } = "";

    /// <summary>Кто. null — сам экземпляр: старт приложения, расписание, фоновая служба.</summary>
    public Guid? ActorId { get; private set; }

    /// <summary>
    /// Имя автора НА МОМЕНТ действия, а не ссылка на учётную запись.
    ///
    /// Снимок потому, что запись переживает пользователя: учётную запись удаляют, переименовывают и
    /// не переносят резервной копией, а журнал обязан читаться и через год. Имя по ссылке в такой
    /// записи показалось бы пустым — то есть журнал сказал бы «неизвестно кто» ровно там, где это
    /// важнее всего.
    /// </summary>
    public string ActorName { get; private set; } = "";

    /// <summary>Над чем: идентификатор пользователя, типа, роли. Строкой — вид цели у каждого свой.</summary>
    public string? TargetId { get; private set; }

    /// <summary>Как цель называлась тогда: почта пользователя, название типа. Снимок, как и имя автора.</summary>
    public string? TargetLabel { get; private set; }

    /// <summary>Прежнее значение — то, ради чего журнал и заводят. null, если его не было.</summary>
    public string? Before { get; private set; }

    /// <summary>Новое значение или описание изменения.</summary>
    public string? After { get; private set; }

    /// <summary>
    /// Собирает запись. <paramref name="occurredAt" /> задаётся только при восстановлении копии:
    /// приехавшая запись сохраняет своё время, иначе восстановление выдало бы чужие действия за
    /// сегодняшние.
    ///
    /// ⚠️ Имя автора и название цели обрезаются ЗДЕСЬ — в единственном месте, где запись вообще
    /// возникает (issue #980). Оба приходят из полей, длину которых никто не ограничивал:
    /// отображаемое имя в профиле, название типа, название роли. Без обрезки отказ базы 22001
    /// приходил бы ПОСЛЕ удавшегося действия — роль уже сменилась, а запрос отвечает 500. Платой в
    /// <c>IActivityLog</c> заявлена потеря записи, а не падение действия; обрезанный хвост эту
    /// плату соблюдает, отказ вставки — нет.
    /// </summary>
    public static ActivityRecord Create(
        string action, Guid? actorId, string actorName,
        string? targetId = null, string? targetLabel = null,
        string? before = null, string? after = null,
        Guid? id = null, DateTimeOffset? occurredAt = null) => new()
        {
            Id = id ?? Guid.NewGuid(),
            OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
            Action = action,
            ActorId = actorId,
            ActorName = Fit(actorName, ActorNameMax) ?? "",
            TargetId = targetId,
            TargetLabel = Fit(targetLabel, TargetLabelMax),
            Before = before,
            After = after,
        };

    /// <summary>
    /// Укоротить до ширины колонки, пометив обрез многоточием: строка «Иван Иванов…» читается как
    /// урезанная, а молча срезанная — как настоящее имя. Прежнее и новое значение не трогаем: их
    /// длина не ограничена нарочно (см. настройку EF).
    /// </summary>
    private static string? Fit(string? value, int max) =>
        value is not null && value.Length > max ? value[..(max - 1)] + "…" : value;
}
