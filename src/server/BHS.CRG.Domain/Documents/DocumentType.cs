using System.Text.Json;
using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Documents;

public enum DocumentTypeKind { Document, Composite }

public class DocumentType : Entity
{
    public string Name { get; private set; } = default!;
    public string Code { get; private set; } = default!;
    public DocumentTypeKind Kind { get; private set; }
    public Guid? ParentId { get; private set; }

    /// <summary>Абстрактный тип нельзя добавить в комплект напрямую — он используется как базовый.</summary>
    public bool IsAbstract { get; private set; }

    /// <summary>Разрешает роль/прокси (issue #89): объект этого типа может ссылаться (`_baseRef`) на
    /// ДРУГОЙ объект ТОГО ЖЕ типа как на реального носителя данных (делегирование, не наследование
    /// по типам). Opt-in — по умолчанию выключено.</summary>
    public bool AllowsProxy { get; private set; }

    /// <summary>Произвольная группа для отображения на странице типов (null — без группы).</summary>
    public string? Group { get; private set; }

    /// <summary>
    /// Кто владеет типом: код модуля (<c>id</c>, <c>work</c>) или <see cref="TypeOwner.Core" />
    /// (ТЗ CORE-18, CORE-30). Обязателен — «ничей» тип означал бы, что при выключении модуля
    /// решать его судьбу придётся догадкой.
    ///
    /// Владелец управляет ПОРЯДКОМ: типы выключенного модуля не предлагаются в редакторе и в
    /// пикерах. Тайной он не управляет — см. <see cref="Visibility" />.
    /// </summary>
    public string Module { get; private set; } = default!;

    /// <summary>Где лежат объекты типа (ТЗ CORE-16).</summary>
    public TypeStorage Storage { get; private set; }

    /// <summary>
    /// Насколько администратор правит схему (ТЗ CORE-19). Объявляет МОДУЛЬ для своих типов;
    /// тип, заведённый человеком, открыт — его поля и так его собственные.
    /// </summary>
    public SchemaEditLevel EditLevel { get; private set; }

    /// <summary>
    /// Видно ли объекты типа за пределами модуля-владельца.
    ///
    /// ⚠️ **Сегодня признак не действует ни на один путь чтения** и заведён заранее, вместе с
    /// владельцем: включают его работой `STG-11` (перевод полутора десятков мест, читающих общую
    /// таблицу, на единую проверку). До неё правило одно — того, что показывать нельзя, в общей
    /// таблице не держат.
    /// </summary>
    public TypeVisibility Visibility { get; private set; }

    /// <summary>
    /// Каналы чтения, которым закрытый тип всё же открыт: коды из <c>TypeReadChannels</c> («общие
    /// данные», MCP, резервная копия, поиск сопоставления, индекс ссылок, наборы данных). Пусто —
    /// не открыт никому. У типа с видимостью <see cref="TypeVisibility.Shared" /> список не
    /// спрашивают: общему типу открыты все каналы.
    /// </summary>
    public IReadOnlyList<string> ReadChannels { get; private set; } = [];

    /// <summary>Схема: { fields, groups?, excludedFields?, fieldOverrides? }</summary>
    public JsonDocument Schema { get; private set; } = default!;

    public JsonDocument PluginBindings { get; private set; } = JsonDocument.Parse("[]");

    private DocumentType() { }

    /// <summary>
    /// ⚠️ Владелец и видимость — ОБЯЗАТЕЛЬНЫЕ параметры, без умолчания. Умолчание здесь было бы
    /// решением, принятым за того, кто заводит тип: у типа, объявленного модулем, и у типа,
    /// заведённого человеком в редакторе, ответы разные (закрытый против общего), а выглядели бы
    /// они одинаково — как пропущенный аргумент.
    /// </summary>
    public static DocumentType Create(
        string name, string code, DocumentTypeKind kind, Guid? parentId, JsonDocument schema,
        string module, TypeVisibility visibility, bool isAbstract = false,
        TypeStorage storage = TypeStorage.SharedObject,
        SchemaEditLevel editLevel = SchemaEditLevel.Open)
        => new()
        {
            Name = name, Code = code, Kind = kind, ParentId = parentId, Schema = schema,
            IsAbstract = isAbstract, Module = module, Visibility = visibility, Storage = storage,
            EditLevel = editLevel,
        };

    /// <summary>
    /// Восстановление из сохранённого состояния. Владелец здесь — необязательный параметр, и это
    /// не послабление: восстанавливают ещё и из копий, снятых ДО появления владельца. Чем их
    /// заполнить, решает вызывающий (<c>BackupService</c> применяет то же правило, что миграция),
    /// а не умолчание, спрятанное в сущности.
    /// </summary>
    public static DocumentType Restore(
        Guid id, string name, string code, DocumentTypeKind kind, Guid? parentId,
        JsonDocument schema, JsonDocument pluginBindings, bool isAbstract,
        DateTimeOffset createdAt, DateTimeOffset updatedAt, string? group = null, bool allowsProxy = false,
        string module = TypeOwner.Core, TypeStorage storage = TypeStorage.SharedObject,
        TypeVisibility visibility = TypeVisibility.Shared, IReadOnlyList<string>? readChannels = null,
        SchemaEditLevel editLevel = SchemaEditLevel.Open)
        => new()
        {
            Id = id, Name = name, Code = code, Kind = kind, ParentId = parentId,
            Schema = schema, PluginBindings = pluginBindings, IsAbstract = isAbstract,
            CreatedAt = createdAt, UpdatedAt = updatedAt, Group = group, AllowsProxy = allowsProxy,
            Module = module, Storage = storage, Visibility = visibility, ReadChannels = readChannels ?? [],
            EditLevel = editLevel,
        };

    public void UpdateSchema(JsonDocument schema) { Schema = schema; TouchUpdatedAt(); }

    /// <summary>
    /// Копия типа с ДРУГОЙ схемой — не сущность БД, а проекция «что будет, если сохранить»
    /// (issue #584). Идентификатор и родитель сохраняются: расчёты, зависящие от схемы, идут по
    /// цепочке наследования и по составу типов, поэтому подменять их нельзя.
    ///
    /// Нужна потому, что менять схему у загруженной сущности ради предпросчёта опасно: это
    /// отслеживаемый объект, и чужой SaveChanges в том же запросе записал бы черновик в базу.
    /// </summary>
    public DocumentType WithSchema(JsonDocument schema)
    {
        var copy = (DocumentType)MemberwiseClone();
        copy.Schema = schema;
        return copy;
    }
    public void Rename(string name, string code) { Name = name; Code = code; TouchUpdatedAt(); }
    public void SetParent(Guid? parentId) { ParentId = parentId; TouchUpdatedAt(); }
    public void UpdatePluginBindings(JsonDocument bindings) { PluginBindings = bindings; TouchUpdatedAt(); }
    public void SetAbstract(bool isAbstract) { IsAbstract = isAbstract; TouchUpdatedAt(); }
    public void SetAllowsProxy(bool allowsProxy) { AllowsProxy = allowsProxy; TouchUpdatedAt(); }
    public void SetGroup(string? group) { Group = string.IsNullOrWhiteSpace(group) ? null : group.Trim(); TouchUpdatedAt(); }

    /// <summary>
    /// Передать тип другому владельцу. Нужно затем, что владельца ставит не только код: типы,
    /// заведённые человеком, достаются ядру, а какие-то из них по смыслу принадлежат модулю — и
    /// переносить их обязано быть чем, иначе единственным способом остаётся правка базы руками.
    /// Проверку «на что тип опирается» делает слой приложения: она смотрит на другие типы.
    /// </summary>
    public void SetOwner(string module) { Module = module; TouchUpdatedAt(); }

    public void SetStorage(TypeStorage storage) { Storage = storage; TouchUpdatedAt(); }

    /// <summary>
    /// Уровень правки схемы. Ставит его МОДУЛЬ, проецируя своё объявление при старте (issue #958),
    /// и никто больше: адреса «сменить уровень» нет и не будет — иначе замок открывался бы тем же
    /// ключом, которым заперт.
    /// </summary>
    public void SetLevel(SchemaEditLevel level) { EditLevel = level; TouchUpdatedAt(); }

    /// <summary>Видимость и каналы задаются вместе: список каналов без видимости ничего не значит.</summary>
    public void SetSharing(TypeVisibility visibility, IReadOnlyList<string>? readChannels)
    {
        Visibility = visibility;
        ReadChannels = readChannels ?? [];
        TouchUpdatedAt();
    }
}
