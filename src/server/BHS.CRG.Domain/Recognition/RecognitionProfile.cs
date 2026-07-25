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

    /// <summary>Шапка счёта на оплату — <c>BuildInvoicePrompt</c> (вызывается вместе с LineItems).</summary>
    InvoiceHeader = 3,

    /// <summary>Таблица товаров счёта — колонки вложенного массива в том же вызове.</summary>
    InvoiceLineItems = 4,

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

    /// <summary>JSON-массив полей: <c>[{ name, description, type, options?, isSystem }]</c>.
    /// Порядок значим — в этом порядке поля печатаются в промпт.</summary>
    public JsonDocument Fields { get; private set; } = default!;

    /// <summary>JSON-объект флагов формы таблицы; null для не-табличных видов.</summary>
    public JsonDocument? Shape { get; private set; }

    /// <summary>Профиль поставляется системой (пришёл сидингом), а не создан пользователем.</summary>
    public bool IsBuiltIn { get; private set; }

    /// <summary>Встроенный профиль правился пользователем — ре-сидинг его больше не трогает.</summary>
    public bool IsModified { get; private set; }

    private RecognitionProfile() { }

    public static RecognitionProfile Create(
        string name, RecognitionProfileKind kind, JsonDocument fields, JsonDocument? shape = null)
        => new() { Name = name.Trim(), Kind = kind, Fields = fields, Shape = shape };

    /// <summary>Встроенный профиль (сидинг). Не помечен как правленый — до первой правки пользователем.</summary>
    public static RecognitionProfile CreateBuiltIn(
        string code, string name, RecognitionProfileKind kind, JsonDocument fields, JsonDocument? shape = null)
        => new()
        {
            Code = code, Name = name.Trim(), Kind = kind, Fields = fields, Shape = shape,
            IsBuiltIn = true,
        };

    /// <summary>Правка пользователем. Вид не меняется — он определяет применяемую стратегию/промпт,
    /// смена вида сделала бы профиль другой сущностью (создайте новый).</summary>
    public void Update(string name, JsonDocument fields, JsonDocument? shape)
    {
        Name = name.Trim();
        Fields = fields;
        Shape = shape;
        if (IsBuiltIn) IsModified = true;
        TouchUpdatedAt();
    }

    /// <summary>Обновление встроенного профиля сидингом (только для не тронутых пользователем).</summary>
    public void ApplySeed(string name, JsonDocument fields, JsonDocument? shape)
    {
        Name = name.Trim();
        Fields = fields;
        Shape = shape;
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
        JsonDocument fields, JsonDocument? shape, bool isBuiltIn, bool isModified,
        DateTimeOffset createdAt, DateTimeOffset updatedAt)
        => new()
        {
            Id = id, Name = name, Code = code, Kind = kind,
            Fields = fields, Shape = shape, IsBuiltIn = isBuiltIn, IsModified = isModified,
            CreatedAt = createdAt, UpdatedAt = updatedAt,
        };
}
