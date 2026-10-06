using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Objects;

namespace BHS.CRG.Application.Common;

/// <summary>
/// Специализированный репозиторий <see cref="DomainObject"/> (issue #84): загрузка документов
/// комплекта с документной фасетой (у общих данных фасеты нет). Обычные CRUD — из <see cref="IRepository{T}"/>;
/// <see cref="IRepository{T}.GetByIdAsync"/> здесь грузит фасету и её файлы.
/// </summary>
public interface IDomainObjectRepository : IRepository<DomainObject>
{
    /// <summary>Документы комплекта — объекты на оси (Set, setId), у которых есть фасета.
    /// <paramref name="tracked"/>=true — с трекингом и фасетой (для массовых изменений порядка/т.п.).</summary>
    Task<IReadOnlyList<DomainObject>> GetSetDocumentsAsync(Guid setId, bool tracked, CancellationToken ct = default);

    /// <summary>Документы нескольких комплектов (untracked, для списков/пикеров).</summary>
    Task<IReadOnlyList<DomainObject>> GetDocumentsInSetsAsync(IReadOnlyCollection<Guid> setIds, CancellationToken ct = default);

    /// <summary>Все документы заданного типа (с трекингом и фасетой+файлами) — для инвалидации
    /// вывода при изменении шаблона (issue #362): фильтр по пинам делается в памяти.</summary>
    Task<IReadOnlyList<DomainObject>> GetDocumentsOfTypeAsync(Guid documentTypeId, CancellationToken ct = default);

    /// <summary>Число документов по каждому комплекту (лёгкий COUNT, без JSONB) — для счётчиков навигации
    /// и каскадов удаления в дереве стройки. Комплекты без документов в словарь не попадают.</summary>
    Task<IReadOnlyDictionary<Guid, int>> CountDocumentsInSetsAsync(IReadOnlyCollection<Guid> setIds, CancellationToken ct = default);

    /// <summary>
    /// Число ГОТОВЫХ документов по паре (комплект, тип) — для процента готовности по плану (#796).
    ///
    /// Готовый — со статусом <c>Generated</c>. Черновик и неудача не готовы: план считает
    /// выпущенные документы, а не заведённые карточки. Одним группирующим запросом, без JSONB:
    /// стройка с сотней комплектов иначе означала бы сотню обращений на каждую отрисовку шапки.
    /// Пары без готовых документов в словарь не попадают.
    /// </summary>
    Task<IReadOnlyDictionary<(Guid SetId, Guid TypeId), int>> CountReadyDocumentsByTypeAsync(
        IReadOnlyCollection<Guid> setIds, CancellationToken ct = default);

    /// <summary>
    /// Записи ОБЩИХ ДАННЫХ ссылками — имя и идентификатор, без JSONB (issue #1078). Тем же приёмом,
    /// что <see cref="CountDocumentsInSetsAsync" />: выборка, в которой данные записи не нужны, не
    /// имеет права их грузить.
    ///
    /// <para>Отбор — вид (и его подтипы, развёрнутые вызывающим), часть названия ИЛИ альтернативного
    /// имени без учёта регистра (issue #1169). Отсечение <paramref name="limit" /> идёт ПОСЛЕ
    /// сортировки по названию — иначе «первые N» означало бы «произвольные N».</para>
    ///
    /// <para>Это ВЫБОР: архивных записей в ответе нет, а сколько их подошло бы под тот же отбор —
    /// названо числом (issue #1185).</para>
    /// </summary>
    Task<ChoiceCandidates> SearchForChoiceAsync(
        IReadOnlyCollection<Guid> typeIds, string? search, int? limit, CancellationToken ct = default);

    /// <summary>
    /// Записи общих данных ссылками по известным идентификаторам — для показа уже стоящих ссылок.
    /// Все, включая архивные; признак — в каждой ссылке (issue #1185).
    /// </summary>
    Task<IReadOnlyList<CommonDataRef>> RefsByIdsAsync(
        IReadOnlyCollection<Guid> typeIds, IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>
    /// Какие из названных записей общих данных лежат в архиве (issue #1185). Вид записи не спрашивает:
    /// спрашивает форма, а в ней стоят ссылки на записи разных видов. С названием: отказ правила
    /// записи обязан назвать запись так, как она названа в справочнике.
    /// </summary>
    Task<IReadOnlyList<ArchivedRecord>> ArchivedAmongAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>
    /// Сохранить правку объекта, собранную по версии <paramref name="seen" /> (issue #1214): строка
    /// блокируется, её версия сверяется с названной, и только потом идёт запись — сверка и запись
    /// неразрывны. Не совпала — <see cref="BHS.CRG.Domain.Common.ConflictException" />, и не записано
    /// ничего.
    ///
    /// <para>Сверки при чтении мало: два сохранения, пришедшие почти разом, оба прочли бы запись, оба
    /// прошли бы сверку и оба записали — побеждало бы последнее, и оба получали бы «сохранено».</para>
    /// </summary>
    Task SaveSeenAsync(DomainObject entry, string seen, CancellationToken ct = default);

    /// <summary>
    /// Прочитать объекты ПОД БЛОКИРОВКОЙ строк — для писателя, который правит данные не по версии, а
    /// преобразованием: перенос ключа поля, починка, перенос картинок, штамп выпуска (issue #1232).
    ///
    /// <para>Такой писатель работал по схеме «прочитал — изменил — сохранил»: правка формы,
    /// сохранённая между его чтением и записью, стиралась его снимком, и оба получали успех. Здесь
    /// чтение идёт уже под блокировкой, и преобразование ложится на то, что лежит в базе сейчас.
    /// Отказа не получает никто; форма, открытая до такой записи, получит свой 409 — версия строки
    /// сдвинулась.</para>
    ///
    /// <para>Объекты приходят с фасетой и файлами, как у <c>GetByIdAsync</c>. ⚠️ У объекта, который
    /// этот контекст прочитал РАНЬШЕ, освежаются поля самой строки — те, что вызывающий не менял;
    /// его несохранённые правки остаются, а данные, изменённые до блокировки, — отказ. Фасета такого
    /// объекта остаётся прежней: кому нужен свежий документ целиком, блокирует ДО первого чтения.</para>
    ///
    /// <para>Блокировка живёт до <see cref="ILockedObjects.SaveAsync" /> либо до освобождения без
    /// сохранения — тогда транзакция откатывается, а изменённое под блокировкой в строках этих
    /// объектов возвращается к прочитанному (фасеты это не касается). Строки, которых уже нет, в
    /// ответ не попадают.</para>
    /// </summary>
    Task<ILockedObjects> ReadForUpdateAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>То же — по условию: строки отбираются, блокируются и только потом читаются.</summary>
    Task<ILockedObjects> ReadForUpdateAsync(
        System.Linq.Expressions.Expression<Func<DomainObject, bool>> which, CancellationToken ct = default);
}

/// <summary>
/// Объекты, прочитанные под блокировкой строк (см. <see cref="IDomainObjectRepository.ReadForUpdateAsync(IReadOnlyCollection{Guid}, CancellationToken)" />).
/// Освобождение без <see cref="SaveAsync" /> откатывает начатое.
/// </summary>
public interface ILockedObjects : IAsyncDisposable
{
    /// <summary>Свежие объекты, отслеживаемые контекстом; порядок не обещан.</summary>
    IReadOnlyList<DomainObject> Objects { get; }

    /// <summary>
    /// Записать накопленное в контексте и отпустить блокировку. ⚠️ Сохраняется ВСЁ накопленное, не
    /// только эти объекты, — одной транзакцией с ними.
    /// </summary>
    Task SaveAsync(CancellationToken ct = default);
}

/// <summary>Запись общих данных, лежащая в архиве: идентификатор и название.</summary>
public readonly record struct ArchivedRecord(Guid Id, string? DisplayName);
