using BHS.CRG.Application.DataSets;

namespace BHS.CRG.Infrastructure.DataSets;

// GostGroupKind определён в Application.DataSets (используется и DTO, и здесь) — Document=0
// обязателен для толерантной миграции старого формата (см. DataSetPdfRecognitionService.ParseGrouping).

/// <summary>
/// Сериализуемое содержимое <see cref="Domain.DataSets.DataSetSource.GostGrouping"/> (JSONB) —
/// ЕДИНАЯ постраничная группировка всех страниц исходного PDF ГОСТ-профиля: обложка, титульный лист
/// и документы как группы с <see cref="GostGroupKind"/>. Это источник истины, из которого
/// проецируются три источника (обложка/титул/документы, см. GostGroupingProjection). Хранит поля
/// каждой страницы (<see cref="GostGroupingPage.Fields"/>), чтобы проекция при ручной правке была
/// без потерь (перенос страницы между группами сохраняет её реальные распознанные поля).
/// </summary>
/// <param name="Groups">Группы страниц, в порядке появления.</param>
/// <param name="ManuallyEdited">
/// true — пользователь применил ручную правку через PUT .../grouping. Повторное автораспознавание
/// затирает это состояние (frontend обязан спросить подтверждение — см. 409 Conflict в эндпоинте).
/// </param>
public record GostGroupingData(IReadOnlyList<GostGroupingGroup> Groups, bool ManuallyEdited);

/// <param name="Kind">Вид группы (документ/обложка/титул).</param>
/// <param name="Code">Шифр документа (или "(без шифра)"); для обложки/титула — null.</param>
/// <param name="Name">Наименование документа, если распознано; для обложки/титула — null.</param>
/// <param name="Pages">Страницы группы с их распознанными полями, в порядке следования.</param>
/// <param name="Tags">Функциональные тэги документа (тип таблицы — спецификация/кабельный журнал,
/// см. FunctionalTag.GostDoc*). Авто-подсказка по НаименованиеДокумента, правится пользователем.</param>
/// <param name="Id">Стабильный идентификатор группы (issue #28) — переживает перераспознавание и
/// ручную правку, служит ключом производных источников-таблиц (вместо дрейфующего firstPageIndex).
/// default (Guid.Empty) — ещё не присвоен (старый формат/новая группа); присваивается при сборке.</param>
public record GostGroupingGroup(
    GostGroupKind Kind, string? Code, string? Name, IReadOnlyList<GostGroupingPage> Pages,
    IReadOnlyList<string>? Tags = null, Guid Id = default);

/// <param name="PageIndex">Индекс страницы исходного PDF (0-based).</param>
/// <param name="Fields">Распознанные поля штампа этой страницы (без служебных ТипСтраницы/Форма).</param>
public record GostGroupingPage(int PageIndex, IReadOnlyDictionary<string, string?> Fields);
