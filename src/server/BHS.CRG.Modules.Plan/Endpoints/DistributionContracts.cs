using System.Text.Json.Serialization;

namespace BHS.CRG.Modules.Plan.Endpoints;

/// <summary>Количество в обеих единицах: строки, как в файле, и позиции плана; у несопоставленной строки — только первое.</summary>
public sealed record QuantityDto(decimal Row, decimal? Plan);

/// <summary>Единицы строки: расценки, как в файле («100 м»), и позиции плана («м»); <c>null</c> — не известна.</summary>
public sealed record LineUnitsDto(string? Row, string? Plan);

/// <summary>Цель распределения: <c>whole</c> — стройка в целом, <c>section</c> — раздел стройки.</summary>
public sealed record DistributionTargetDto(string Kind, Guid? SectionId);

public sealed record DistributionPartDto(Guid SectionId, QuantityDto Quantity);

/// <summary>Счётчики распределения: <c>distributed + partial + whole + undistributed = rows</c> (D17 зонтика).</summary>
/// <param name="Whole">Строк под явной «стройкой в целом».</param>
/// <param name="CarryOverPending">Позиций отчёта переноса, ждущих решения.</param>
/// <param name="ChangedAt">Когда распределение меняли последний раз; <c>null</c> — не распределяли.</param>
public sealed record RevisionDistributionDto(
    int Rows, int Distributed, int Partial, int Whole, int Undistributed, int CarryOverPending, DateTimeOffset? ChangedAt);

/// <summary>Почему распределение недоступно: <c>estimate-section</c> — смета составлена на раздел стройки.</summary>
public sealed record DistributionUnavailableDto(string Code, Guid SectionId, string? SectionName);

/// <summary>Раздел стройки — цель распределения — с итогом по ней.</summary>
/// <param name="Name">Название раздела; <c>null</c> — раздела в ядре больше нет.</param>
/// <param name="Deleted">Раздела в стройке больше нет, а распределение на него есть — тоном warning.</param>
/// <param name="Rows">Строк, которые идут на раздел целиком или частью.</param>
public sealed record DistributionSectionTotalDto(Guid SectionId, string? Name, bool Deleted, int Rows);

/// <summary>Итог по целям строкой над таблицей (П2): разделы, «стройка в целом» и «не распределено».</summary>
/// <param name="WholeRows">Строк, у которых часть объёма идёт в «стройку в целом» — правилом или остатком.</param>
public sealed record DistributionTotalsDto(
    IReadOnlyList<DistributionSectionTotalDto> Sections, int WholeRows, int UndistributedRows);

/// <summary>Правило раздела ЛСР со счётчиками его строк.</summary>
/// <param name="LsrSectionCode">Номер главы в файле — ключ правила в этой редакции; <c>null</c> — строки вне разделов.</param>
/// <param name="Target">Цель; <c>null</c> — правила нет, «не распределено».</param>
/// <param name="Exceptions">Строк со своими частями.</param>
public sealed record DistributionRuleDto(
    string? LsrSectionCode, string? LsrSectionName, DistributionTargetDto? Target, int Rows, int Exceptions, int Undistributed);

/// <summary>Распределение строки.</summary>
/// <param name="Source"><c>parts</c> — свои части; <c>rule</c> — правило раздела ЛСР; <c>none</c> — ничем.</param>
/// <param name="Status"><c>distributed</c>, <c>partial</c>, <c>whole</c> или <c>undistributed</c>.</param>
/// <param name="Splittable">Части задать можно; нет — причина <see cref="SplitReason" />, а правилом раздела строка
/// распределяется всё равно.</param>
/// <param name="SplitReason"><c>no-volume</c> — у строки нет объёма.</param>
/// <param name="Volume">Объём строки; <c>null</c> — объёма нет.</param>
/// <param name="Rest">Остаток «стройка в целом» у строки с частями.</param>
/// <param name="Targets">Разделы стройки строки — для отбора по цели.</param>
/// <param name="Whole">Часть объёма идёт в «стройку в целом».</param>
public sealed record DistributionLineDto(
    Guid RowId, int Order, string? LsrSectionCode, string Number, string Name, string Kind, bool Subordinate,
    string Source, string Status, bool Splittable,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SplitReason,
    QuantityDto? Volume, LineUnitsDto Units, IReadOnlyList<DistributionPartDto> Parts, QuantityDto? Rest,
    IReadOnlyList<Guid> Targets, bool Whole);

/// <summary>Позиция отчёта переноса.</summary>
/// <param name="Category"><c>shifted</c>, <c>ambiguous</c>, <c>overflow</c> или <c>lost</c>.</param>
/// <param name="Subject"><c>row</c> — части строки; <c>rule</c> — правило раздела ЛСР.</param>
/// <param name="Pending">Ждёт решения; <c>shifted</c> решения не требует.</param>
/// <param name="Parts">Прежние части строки источника — в его единице строки.</param>
/// <param name="Target">Цель правила источника — у позиции-правила.</param>
/// <param name="RowId">Строка новой редакции, с которой позиция сопоставлена (<c>shifted</c>, <c>overflow</c>).</param>
/// <param name="NewVolume">Новый объём строки — у <c>overflow</c>.</param>
/// <param name="Candidates">Строки-кандидаты — у <c>ambiguous</c>.</param>
public sealed record CarryOverItemDto(
    Guid Id, string Category, string Subject, bool Pending, string? LsrSectionCode, string? LsrSectionName,
    string? RateCode, string? NumberInSection, string? Number, string? Name, decimal? SourceVolume, string? SourceUnit,
    IReadOnlyList<DistributionPartDto> Parts, DistributionTargetDto? Target, Guid? RowId, decimal? NewVolume,
    IReadOnlyList<Guid> Candidates);

/// <summary>Числа переноса по категориям — и в ответе создания редакции, и в отчёте у редакции.</summary>
/// <param name="SourceRevisionId">Редакция-источник; <c>null</c> — с тех пор удалена (номер остаётся).</param>
public sealed record CarryOverCountsDto(
    Guid? SourceRevisionId, int? SourceNumber, int Carried, int Shifted, int Ambiguous, int Overflow, int Lost);

/// <summary>Отчёт переноса у редакции: источник, числа и нерешённые позиции.</summary>
public sealed record CarryOverDto(CarryOverCountsDto Counts, int Pending, IReadOnlyList<CarryOverItemDto> Items);

/// <summary>
/// Распределение редакции одним ответом, без страниц (change <c>plan-10-distribution</c>, «Адреса» в
/// <c>design.md</c>): правила, строки, итог по целям и отчёт переноса.
/// </summary>
/// <param name="Available"><c>false</c> — распределять нечего: смета на разделе стройки (<see cref="Reason" />).</param>
/// <param name="Version">Версия распределения — её называет правка заголовком <c>If-Match</c>; со своей версией
/// редакции она не смешивается.</param>
/// <param name="ChangedBy">Кто менял последним — имя на момент правки.</param>
/// <param name="Current">Редакция действующая: не действующая распределяется так же, но «на план не влияет».</param>
/// <param name="Frozen">Редакция зафиксирована: распределение правится, у графика будет пометка.</param>
public sealed record DistributionDto(
    Guid RevisionId, bool Available,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DistributionUnavailableDto? Reason,
    string Version, DateTimeOffset? ChangedAt, string? ChangedBy, bool Current, bool Frozen,
    RevisionDistributionDto Counts, DistributionTotalsDto Targets, IReadOnlyList<DistributionRuleDto> Rules,
    IReadOnlyList<DistributionLineDto> Lines, CarryOverDto? CarryOver);

/// <summary>Прежнее правило раздела ЛСР — для «Отменить».</summary>
public sealed record PreviousRuleDto(string? LsrSectionCode, DistributionTargetDto? Target);

/// <summary>Прежние части строки в форме <c>PUT …/lines</c>: <c>parts: null</c> — строка была «по правилу».</summary>
public sealed record PreviousLineDto(Guid RowId, string Unit, IReadOnlyList<PreviousPartDto>? Parts);

public sealed record PreviousPartDto(Guid SectionId, decimal Quantity);

/// <summary>Прежнее состояние изменённого: им экран делает «Отменить» одной записью <c>PUT …/rules</c> с <c>lines</c>.</summary>
public sealed record DistributionPreviousDto(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreviousRuleDto? Rule,
    IReadOnlyList<PreviousLineDto> Lines);

/// <summary>Ответ записи распределения: новая версия, прежнее состояние изменённого и распределение целиком.</summary>
public sealed record DistributionWriteDto(string Version, DistributionPreviousDto Previous, DistributionDto Distribution);
