using BHS.CRG.Modules.Data;

namespace BHS.CRG.Modules.Ports;

/// <summary>Что стало с записью ядра, на которую ссылается модуль (ТЗ CORE-34.4).</summary>
public enum ReferenceState
{
    /// <summary>Запись на месте.</summary>
    Present,

    /// <summary>Запись в архиве: из выбора нового значения убрана, сохранённые ссылки целы
    /// (issue #1185). Бывает только у записи справочника (<see cref="ReferenceTarget.Record" />):
    /// у стройки, раздела, типа и пользователя архива нет.</summary>
    Archived,

    /// <summary>Записи больше нет. Ссылка модуля на неё — потерянная.</summary>
    Lost,
}

/// <summary>Потерянная ссылка: в колонке модуля стоит идентификатор записи ядра, которой нет.</summary>
/// <param name="Table">Таблица модуля.</param>
/// <param name="Column">Колонка со ссылкой.</param>
/// <param name="Target">Вид цели — как его объявил модуль.</param>
/// <param name="TargetId">Идентификатор, которого в ядре нет.</param>
/// <param name="DocumentKey">Ключ документа-держателя (<see cref="ReferenceDocument.Via" />): счёт у
/// строки счёта. <c>null</c> — у объявления документ не назван.</param>
/// <param name="Rows">Сколько строк таблицы несут эту ссылку в этом документе.</param>
/// <param name="DocumentTable">Таблица документа-держателя (<see cref="ReferenceDocument.Table" />):
/// <c>invoices</c> у строки счёта. Чей это документ, говорит объявление, а не список таблиц у читателя —
/// второй список разошёлся бы с первым на первой же новой таблице (ревью PR #1211).</param>
public sealed record LostReference(
    string Table, string Column, ReferenceTarget Target, Guid TargetId, Guid? DocumentKey, int Rows,
    string? DocumentTable = null);

/// <summary>Почему колонку не удалось проверить.</summary>
public enum UncheckedReason
{
    /// <summary>Вид цели не назван: в колонке JSON цели разные, и искать их негде.</summary>
    MixedTargets,

    /// <summary>База отказала в чтении колонки.</summary>
    Unreadable,

    /// <summary>Колонки в схеме нет либо она не идентификатор: схема отстала от объявления.</summary>
    MissingInSchema,
}

/// <summary>Объявленная держащая колонка, которую опрос НЕ проверил.</summary>
/// <param name="What">Чем колонка названа в объявлении: «счета, где запись выбрана в дополнительном
/// поле».</param>
public sealed record UncheckedColumn(string Table, string Column, UncheckedReason Reason, string What);

/// <summary>Ответ обратного опроса.</summary>
/// <param name="Lost">Потерянные ссылки.</param>
/// <param name="Unchecked">Что не проверено. ⚠️ Пустой <paramref name="Lost" /> при непустом этом
/// списке — НЕ «потерь нет»: показывать его нулём нельзя.</param>
/// <param name="AsOf">Момент снимка базы, по которому дан ответ.</param>
public sealed record LostReferences(
    IReadOnlyList<LostReference> Lost, IReadOnlyList<UncheckedColumn> Unchecked, DateTimeOffset AsOf);

/// <summary>
/// Обратный опрос: существуют ли записи ядра, на которые ссылается модуль (ТЗ CORE-34.2–34.4, issue
/// #1184).
///
/// <para><b>Ядро находит потери, модуль судит о правимости.</b> Что запись закрытого периода править
/// нельзя, знает только модуль, поэтому счётчик «потеряно ссылок» считает он — по ответу этого порта.</para>
///
/// <para><b>Почему не <see cref="IModuleCatalog.RefsAsync" />.</b> Тот отвечает «нет» и на запись,
/// которой нет, и на запись другого вида, и на вид, которого нет в установке: потерю им определять
/// нельзя — без типа «Номенклатура» потерянной выглядела бы каждая строка каждого счёта. Здесь вопрос
/// задаётся таблице ядра по виду цели, а не коду типа.</para>
///
/// <para>Потеря нигде не хранится: ответ вычисляется на чтении. Хранимая пометка устаревала бы ровно
/// там, где потери и возникают, — при восстановлении копии.</para>
/// </summary>
public interface IModuleReferenceTargets
{
    /// <summary>
    /// Состояние каждой спрошенной записи. Ответ ПОЛНЫЙ: ключ есть у каждого идентификатора из
    /// <paramref name="ids" />. «Не знаем» — исключение, а не пропуск: пропуск читался бы как «на месте».
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ReferenceState>> StatesAsync(
        ReferenceTarget target, IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>
    /// Потерянные ссылки модуля — по его объявлениям (<see cref="IAppModule.References" />), в одном
    /// снимке базы. Опрашиваются держащие колонки; помнящие (<see cref="ModuleReference.Remembering" />)
    /// — нет: удаление их цели законно.
    /// </summary>
    Task<LostReferences> LostAsync(string moduleCode, CancellationToken ct = default);
}
