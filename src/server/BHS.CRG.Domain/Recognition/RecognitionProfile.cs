using System.Text.Json;
using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Recognition;

/// <summary>
/// Вид профиля распознавания (issue #405/#406). Выбирает СТРАТЕГИЮ — конкретный хардкод-промпт из
/// <c>RecognitionShared</c>; профиль даёт этой стратегии ПАРАМЕТРЫ (набор полей, флаги формы).
/// Тот же «мост конфиг↔хардкод», что <c>FunctionalTag</c>/<c>TagRegistry</c>: конфигурация ВЫБИРАЕТ
/// известную коду стратегию, а не пишет её. Расширяется только вместе с новым билдером промпта.
/// Хранится строкой (конвенция проекта — enum'ы в БД строками, не int).
/// </summary>
public enum RecognitionProfileKind
{
    /// <summary>Основная надпись (штамп) листа по ГОСТ Р 21.101-2020 — <c>BuildTitleBlockPrompt</c>.</summary>
    TitleBlock = 1,

    /// <summary>Заглавный лист комплекта (обложка/титул) — <c>BuildCoverTitlePrompt</c>.</summary>
    CoverTitle = 2,

    /// <summary>Счёт на оплату целиком — <c>BuildInvoicePrompt</c>: шапка в <c>Fields</c>, товары в
    /// <c>RowColumns</c>. Один вид на один вызов распознавания: делить шапку и товары на два вида
    /// нельзя — код был бы обязан знать, что их надо склеить, и вид перестал бы выбирать стратегию.</summary>
    Invoice = 3,

    /// <summary>Таблица документа (спецификация/ведомость и произвольные формы) — <c>BuildTablePrompt</c>.</summary>
    Table = 5,

    /// <summary>Кабельный журнал — <c>BuildCableJournalPrompt</c> (двойная форма проект/факт).</summary>
    CableJournal = 6,
}

/// <summary>
/// Именованный набор параметров распознавания. Промпты остаются хардкодом (их пишем мы) — профиль
/// задаёт к ним параметры: перечень полей/колонок (имя + описание + тип) и, для табличных видов,
/// флаги формы таблицы. Привязывается к файлу набора и к группе листов, переиспользуется между
/// проектами.
///
/// Встроенные профили (<see cref="IsBuiltIn"/>) сидируются из констант кода и покрывают функционал
/// текущей версии. Апсерт при старте идёт по <see cref="Code"/> и ТОЛЬКО пока
/// <see cref="IsModified"/> == false — так улучшения дефолтов в новых версиях доезжают до всех, кто
/// профиль не трогал, а правка пользователя больше не затирается.
/// </summary>
public class RecognitionProfile : Entity
{
    public string Name { get; private set; } = default!;

    /// <summary>Стабильный код встроенного профиля (ключ ре-сидинга). null у пользовательских.</summary>
    public string? Code { get; private set; }

    public RecognitionProfileKind Kind { get; private set; }

    /// <summary>JSON-массив СКАЛЯРНЫХ полей: <c>[{ name, description, type, options? }]</c>.
    /// Порядок значим — в этом порядке поля печатаются в промпт. Признак «системное» здесь НЕ
    /// хранится: он производный от кодового дескриптора вида, иначе снимался бы через импорт/бэкап
    /// и защита несущих полей обходилась бы.</summary>
    public JsonDocument Fields { get; private set; } = default!;

    /// <summary>JSON-массив колонок ТАБЛИЧНОЙ части того же вызова (строки счёта, строки таблицы
    /// документа) в том же формате, что <see cref="Fields"/>; null — табличной части нет.
    /// Ключ, под которым модель возвращает массив строк, задаёт дескриптор вида, а не данные.</summary>
    public JsonDocument? RowColumns { get; private set; }

    /// <summary>JSON-объект флагов формы таблицы; null для не-табличных видов.</summary>
    public JsonDocument? Shape { get; private set; }

    /// <summary>Профиль поставляется системой (пришёл сидингом), а не создан пользователем.</summary>
    public bool IsBuiltIn { get; private set; }

    /// <summary>Встроенный профиль правился пользователем — ре-сидинг его больше не трогает.</summary>
    public bool IsModified { get; private set; }

    /// <summary>Хеш ЗАВОДСКОГО содержимого на момент последнего сида. Позволяет заметить, что
    /// встроенный профиль улучшился в новой версии, пока пользователь держит свою правку:
    /// сидер не перезаписывает, но выставляет <see cref="BuiltInOutdated"/>. Без этого
    /// <see cref="IsModified"/> молча замораживал бы профиль ЦЕЛИКОМ — правка одного описания
    /// отключала бы будущие улучшения всех остальных полей.</summary>
    public string? BuiltInHash { get; private set; }

    /// <summary>Заводская версия профиля изменилась, а пользовательская правка сохранена — повод
    /// показать «обновился, посмотреть отличия / сбросить к заводским».</summary>
    public bool BuiltInOutdated { get; private set; }

    private RecognitionProfile() { }

    public static RecognitionProfile Create(
        string name, RecognitionProfileKind kind,
        JsonDocument fields, JsonDocument? rowColumns = null, JsonDocument? shape = null)
        => new() { Name = name.Trim(), Kind = kind, Fields = fields, RowColumns = rowColumns, Shape = shape };

    /// <summary>Встроенный профиль (сидинг). Не помечен как правленый — до первой правки пользователем.</summary>
    public static RecognitionProfile CreateBuiltIn(
        string code, string name, RecognitionProfileKind kind,
        JsonDocument fields, JsonDocument? rowColumns, JsonDocument? shape, string builtInHash)
        => new()
        {
            Code = code, Name = name.Trim(), Kind = kind,
            Fields = fields, RowColumns = rowColumns, Shape = shape,
            IsBuiltIn = true, BuiltInHash = builtInHash,
        };

    /// <summary>Правка пользователем. Вид не меняется — он определяет применяемую стратегию/промпт,
    /// смена вида сделала бы профиль другой сущностью (создайте новый).</summary>
    public void Update(string name, JsonDocument fields, JsonDocument? rowColumns, JsonDocument? shape)
    {
        Name = name.Trim();
        Fields = fields;
        RowColumns = rowColumns;
        Shape = shape;
        if (IsBuiltIn) IsModified = true;
        TouchUpdatedAt();
    }

    /// <summary>Обновление встроенного профиля сидингом (только для не тронутых пользователем).</summary>
    public void ApplySeed(
        string name, JsonDocument fields, JsonDocument? rowColumns, JsonDocument? shape, string builtInHash)
    {
        Name = name.Trim();
        Fields = fields;
        RowColumns = rowColumns;
        Shape = shape;
        BuiltInHash = builtInHash;
        BuiltInOutdated = false;
        TouchUpdatedAt();
    }

    /// <summary>Заводская версия ушла вперёд, но пользовательская правка сохраняется — только отмечаем.</summary>
    public void MarkBuiltInOutdated()
    {
        if (BuiltInOutdated) return;
        BuiltInOutdated = true;
        TouchUpdatedAt();
    }

    /// <summary>«Сбросить к заводским»: снимает отметку о правке, чтобы ближайший сидинг вернул дефолт.</summary>
    public void ResetToBuiltIn()
    {
        if (!IsBuiltIn) return;
        IsModified = false;
        TouchUpdatedAt();
    }

    public static RecognitionProfile Restore(
        Guid id, string name, string? code, RecognitionProfileKind kind,
        JsonDocument fields, JsonDocument? rowColumns, JsonDocument? shape,
        bool isBuiltIn, bool isModified, string? builtInHash, bool builtInOutdated,
        DateTimeOffset createdAt, DateTimeOffset updatedAt)
        => new()
        {
            Id = id, Name = name, Code = code, Kind = kind,
            Fields = fields, RowColumns = rowColumns, Shape = shape,
            IsBuiltIn = isBuiltIn, IsModified = isModified,
            BuiltInHash = builtInHash, BuiltInOutdated = builtInOutdated,
            CreatedAt = createdAt, UpdatedAt = updatedAt,
        };
}
