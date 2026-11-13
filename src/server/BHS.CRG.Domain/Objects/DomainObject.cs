using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Domain.Objects;

/// <summary>
/// Единый объект предметной области (issue #84, Фаза 1) — экземпляр составного типа на единой оси
/// расположения. Заменяет прежние <c>CommonDataEntry</c> (общие данные) и <c>DocumentInstance</c>
/// (документ комплекта): различие — только в <see cref="DocumentType.Kind"/> типа и в наличии
/// <see cref="Facet"/> (документная фасета есть ⟺ тип — Document). Расположение — единая пара
/// (<see cref="ScopeLevel"/>, <see cref="ScopeId"/>); документ живёт в (Set, setId).
/// </summary>
public class DomainObject : Entity
{
    /// <summary>Имя экземпляра. Null — отображается имя типа (для документов = прежний Name).</summary>
    public string? DisplayName { get; private set; }

    /// <summary>Альтернативные имена (issue #74) — участвуют в сопоставлении по имени наравне с DisplayName.</summary>
    public List<string> Aliases { get; private set; } = [];

    /// <summary>ID типа (<see cref="DocumentType"/>) — Composite или Document.</summary>
    public Guid CompositeTypeId { get; private set; }

    /// <summary>Данные полей экземпляра (реквизиты) + возможный «_baseRef».</summary>
    public JsonDocument Data { get; private set; } = JsonDocument.Parse("{}");

    /// <summary>Уровень расположения на единой оси.</summary>
    public CatalogScope ScopeLevel { get; private set; }

    /// <summary>Носитель уровня (null для System). Для документов = SetId.</summary>
    public Guid? ScopeId { get; private set; }

    /// <summary>Документная фасета — только у объектов Document-типа. Null ⇒ объект общих данных.</summary>
    public DocumentFacet? Facet { get; private set; }

    public bool IsDocument => Facet is not null;

    /// <summary>
    /// Когда запись отправили в архив (issue #1185, ТЗ CORE-34.4). Null — запись действующая.
    ///
    /// <para>Архив — решение «больше не предлагать»: запись пропадает из мест ВЫБОРА нового значения,
    /// а всё, что на неё уже ссылается, видит её как прежде. Не равен увольнению сотрудника — у того
    /// своя дата в данных типа.</para>
    ///
    /// <para>⚠️ Сеттера и метода «в архив» у сущности нет нарочно. Запись сохраняется ЦЕЛИКОМ и без
    /// версии: форма, открытая до архива и сохранённая после, вернула бы сюда null — и запись молча
    /// вернулась бы в выбор. Поэтому колонку обычное сохранение не пишет вовсе (см.
    /// <c>DomainObjectConfiguration</c>), а меняет её одна служба отдельным обновлением —
    /// <c>IRecordArchive</c>. Значение здесь — то, что прочитано из базы или пришло из копии при
    /// создании записи.</para>
    /// </summary>
    public DateTimeOffset? ArchivedAt { get; private set; }

    public bool IsArchived => ArchivedAt is not null;

    /// <summary>
    /// Версия строки, как она прочитана из базы (issue #1214), — системная колонка PostgreSQL
    /// <c>xmin</c>: её двигает ЛЮБАЯ запись строки, и забыть её поднять нельзя. Форма получает её
    /// с записью и называет при правке; не совпала с лежащей — запись тем временем изменили.
    ///
    /// <para>У только что созданного объекта — ноль до первого сохранения; после сохранения EF
    /// перечитывает её сам.</para>
    /// </summary>
    public uint RowVersion { get; private set; }

    /// <summary>Версия строкой — в таком виде она ходит наружу: это отметка, а не число для счёта.</summary>
    public string Version => RowVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private DomainObject() { }

    public static DomainObject Create(
        Guid compositeTypeId, string? displayName, JsonDocument data,
        CatalogScope scopeLevel, Guid? scopeId, IReadOnlyList<string>? aliases = null)
        => new()
        {
            CompositeTypeId = compositeTypeId,
            DisplayName = Norm(displayName),
            Data = data,
            ScopeLevel = scopeLevel,
            ScopeId = scopeId,
            Aliases = NormalizeAliases(aliases),
        };

    public static DomainObject Restore(
        Guid id, Guid compositeTypeId, string? displayName, JsonDocument data,
        CatalogScope scopeLevel, Guid? scopeId, DateTimeOffset createdAt, DateTimeOffset updatedAt,
        IReadOnlyList<string>? aliases, DateTimeOffset? archivedAt)
        => new()
        {
            Id = id, CompositeTypeId = compositeTypeId, DisplayName = Norm(displayName),
            Data = data, ScopeLevel = scopeLevel, ScopeId = scopeId,
            Aliases = NormalizeAliases(aliases), CreatedAt = createdAt, UpdatedAt = updatedAt,
            // Параметр обязательный, без значения по умолчанию: восстановление, забывшее о признаке,
            // не должно компилироваться. В базу он уйдёт только у НОВОЙ записи — существующей его
            // ставит IRecordArchive (обычное сохранение колонку не пишет).
            ArchivedAt = archivedAt,
        };

    /// <summary>
    /// Восстановление ДОКУМЕНТА из резервной копии (issue #833): объект вместе с документной
    /// фасетой — статусом, порядком, выбранными шаблонами и кэшем плагинов.
    ///
    /// Отдельно от <see cref="Restore" /> потому, что фасета и есть то, чем документ отличается от
    /// записи общих данных: восстановить документ без неё значило бы превратить его в общие данные
    /// — молча и необратимо.
    /// </summary>
    public static DomainObject RestoreDocument(
        Guid id, Guid compositeTypeId, string? displayName, JsonDocument data,
        Guid setId, DateTimeOffset createdAt, DateTimeOffset updatedAt, IReadOnlyList<string>? aliases,
        DocumentStatus status, int sortOrder, Guid? templateId, string? templateIds,
        string? templateParams, JsonDocument pluginData)
    {
        // Документ в архиве не бывает (см. EnsureFacet) — признака у него в копии нет.
        var o = Restore(id, compositeTypeId, displayName, data,
            CatalogScope.Set, setId, createdAt, updatedAt, aliases, archivedAt: null);
        var facet = o.EnsureFacet();
        facet.Status = status;
        facet.SortOrder = sortOrder;
        facet.TemplateId = templateId;
        facet.TemplateIds = templateIds;
        facet.TemplateParams = templateParams;
        facet.PluginData = pluginData;
        return o;
    }

    /// <summary>
    /// Клон как документ (issue #283, дублировать/копировать/перенести): новый объект того же типа
    /// в целевом Set-scope с переданными (уже клонированными/обработанными вызывающим) данными.
    /// Копирует конфиг шаблонов и алиасы; фасета свежая — Draft, без сгенерированных файлов,
    /// PluginData пуст (это generation-кэш, при Draft невалиден). SortOrder выставляет вызывающий.
    /// </summary>
    public static DomainObject CloneAsDocument(DomainObject source, Guid targetSetId, JsonDocument data, string? displayName)
    {
        var clone = Create(source.CompositeTypeId, displayName, data, CatalogScope.Set, targetSetId, source.Aliases);
        clone.EnsureFacet();
        if (source.Facet is not null)
        {
            clone.SetTemplate(source.Facet.TemplateId);
            clone.SetTemplateIds(source.Facet.TemplateIds);
            clone.SetTemplateParams(source.Facet.TemplateParams);
        }
        return clone;
    }

    /// <summary>
    /// Делает объект документом (создаёт фасету, если ещё нет). Возвращает фасету.
    ///
    /// <para>Архивная запись документом не становится: архив документу запрещён (CORE-34.4). Главный
    /// запрет держит служба архива своим условным обновлением; эта дверь — обратная сторона, чтобы
    /// он не обходился в два шага: отправить запись в архив, потом сделать её документом.</para>
    ///
    /// <para>⚠️ Проверка — по значению в памяти. Объект, прочитанный до архива, её пройдёт; и
    /// восстановление копии строит документ заново, не зная, что под этим идентификатором в базе.
    /// Поэтому после документов восстановление зовёт <c>IRecordArchive.ClearOnDocumentsAsync</c>.</para>
    /// </summary>
    public DocumentFacet EnsureFacet()
    {
        // Framework-тип: до этого места доходит только ошибка вызывающего кода, человеку здесь
        // исправлять нечего (как и у Doc ниже).
        if (Facet is null && IsArchived)
            throw new InvalidOperationException("Запись в архиве не может стать документом.");
        Facet ??= DocumentFacet.Create(Id);
        return Facet;
    }

    // ── Общие изменения (обе разновидности) ─────────────────────────────────────
    public void Update(string? displayName, JsonDocument data, IReadOnlyList<string>? aliases = null)
    {
        DisplayName = Norm(displayName);
        Data = data;
        Aliases = NormalizeAliases(aliases);
        TouchUpdatedAt();
    }

    public void Rename(string? displayName) { DisplayName = Norm(displayName); TouchUpdatedAt(); }
    public void SetData(JsonDocument data) { Data = data; TouchUpdatedAt(); }

    /// <summary>Перенос документа в другой комплект (issue #283, фаза D): смена носителя Set-scope.</summary>
    public void MoveToSet(Guid targetSetId) { ScopeLevel = CatalogScope.Set; ScopeId = targetSetId; TouchUpdatedAt(); }

    // ── Документные изменения (через фасету; TouchUpdatedAt на объекте) ──────────
    // Framework-тип намеренно (issue #691): спросить у не-документа его документные свойства — это
    // дефект вызывающего кода, а не то, что пользователь может исправить. Пусть уходит в лог.
    private DocumentFacet Doc => Facet ?? throw new InvalidOperationException("Объект не является документом (нет фасеты).");

    public DocumentStatus Status => Doc.Status;
    public int SortOrder => Doc.SortOrder;
    public Guid? TemplateId => Doc.TemplateId;
    public string? TemplateIds => Doc.TemplateIds;
    public string? TemplateParams => Doc.TemplateParams;
    public JsonDocument PluginData => Doc.PluginData;
    public IReadOnlyList<GeneratedFile> GeneratedFiles => Doc.GeneratedFiles;

    public void MarkGenerating() { Doc.Status = DocumentStatus.Generating; TouchUpdatedAt(); }
    public void MarkFailed() { Doc.Status = DocumentStatus.Failed; TouchUpdatedAt(); }
    public void SetSortOrder(int order) { Doc.SortOrder = order; TouchUpdatedAt(); }
    public void SetTemplate(Guid? templateId) { Doc.TemplateId = templateId; TouchUpdatedAt(); }
    public void SetTemplateIds(string? json) { Doc.TemplateIds = json; TouchUpdatedAt(); }

    // ── Пины шаблонов (issue #362/#364) — единая точка семантики «на что запиннут документ» ──
    private List<Guid> ParsedTemplateIds()
    {
        if (string.IsNullOrWhiteSpace(Doc.TemplateIds)) return [];
        try { return JsonSerializer.Deserialize<List<Guid>>(Doc.TemplateIds) ?? []; }
        catch (JsonException) { return []; }
    }

    /// <summary>Документ запиннут на версию — явным одиночным <see cref="TemplateId"/> или в наборе <see cref="TemplateIds"/>.</summary>
    public bool PinsTemplate(Guid templateId) => Doc.TemplateId == templateId || ParsedTemplateIds().Contains(templateId);

    /// <summary>Нет пина — документ резолвится в default-active шаблон типа.</summary>
    public bool HasNoTemplatePin => Doc.TemplateId is null && ParsedTemplateIds().Count == 0;

    /// <summary>Убирает пин на версию (issue #364): из одиночного <see cref="TemplateId"/> и из набора
    /// <see cref="TemplateIds"/> (пустой набор → null). После снятия документ резолвится в дефолт.</summary>
    public void UnpinTemplate(Guid templateId)
    {
        var changed = false;
        if (Doc.TemplateId == templateId) { Doc.TemplateId = null; changed = true; }
        var ids = ParsedTemplateIds();
        if (ids.Remove(templateId))
        {
            Doc.TemplateIds = ids.Count == 0 ? null : JsonSerializer.Serialize(ids);
            changed = true;
        }
        if (changed) TouchUpdatedAt();
    }
    /// <summary>
    /// Пересаживает закрепления на шаблон: каждая версия из <paramref name="replacedBy"/> заменяется
    /// своей новой (id-template-version-integrity, D6). Закрепление значит «этот шаблон», а не «эта
    /// строка»: иначе новая версия оставляла бы документ на погашенной, и генерация отказывала бы
    /// «ни один из выбранных шаблонов не активен» (#1923).
    /// <list type="bullet">
    /// <item>одиночный <see cref="TemplateId"/> — заменяется;</item>
    /// <item>набор <see cref="TemplateIds"/> — элементы на месте, порядок сохраняется, повтор после
    /// замены убирается;</item>
    /// <item>ключи <see cref="TemplateParams"/> — переносятся: иначе настройки документа, привязанные
    /// к прежнему id, потерялись бы молча. Если у новой версии свои переопределения уже есть, остаются
    /// они.</item>
    /// </list>
    /// Выпущенные файлы пересаживаемых версий переходят вместе с закреплением (см.
    /// <see cref="RetargetFiles" />).
    /// <paramref name="selectionOnly"/> — разовая починка (D8, п. 3): только набор и переопределения
    /// выбранных в нём версий. Одиночный пин тогда не трогается — он и так генерируется, а пересадка
    /// молча сменила бы его PDF. Возвращает, изменилось ли что-нибудь.
    /// </summary>
    public bool RepinTemplates(IReadOnlyDictionary<Guid, Guid> replacedBy, bool selectionOnly = false)
    {
        if (Facet is null || replacedBy.Count == 0) return false;
        var changed = false;
        if (!selectionOnly && Doc.TemplateId is { } single && replacedBy.TryGetValue(single, out var singleTo))
        {
            Doc.TemplateId = singleTo;
            changed = true;
        }

        var ids = ParsedTemplateIds();
        var selected = ids.Where(replacedBy.ContainsKey).ToHashSet();
        if (selected.Count > 0)
        {
            var mapped = new List<Guid>();
            foreach (var id in ids)
            {
                var to = replacedBy.GetValueOrDefault(id, id);
                if (!mapped.Contains(to)) mapped.Add(to);
            }
            Doc.TemplateIds = JsonSerializer.Serialize(mapped);
            changed = true;
        }

        Func<Guid, bool> moves = selectionOnly ? selected.Contains : replacedBy.ContainsKey;
        var moved = RepinParamKeys(id => moves(id) ? replacedBy[id] : null);
        if (moved is not null) { Doc.TemplateParams = moved; changed = true; }
        if (RetargetFiles(replacedBy, moves)) changed = true;
        if (changed) TouchUpdatedAt();
        return changed;
    }

    /// <summary>
    /// Файлы пересаживаемых версий — к новой: файл, помеченный погашенной версией, сборка комплекта
    /// ставила бы в конец, а следующая генерация не заменила бы, а добавила бы второй PDF того же
    /// шаблона. Если у новой версии свой файл того же формата уже есть, прежний не трогается; из
    /// нескольких кандидатов к одной версии переходит самый поздний (при равенстве — больший по тексту
    /// идентификатор). Правило то же, что у SQL миграции TemplateVersionIntegrity.
    /// </summary>
    private bool RetargetFiles(IReadOnlyDictionary<Guid, Guid> replacedBy, Func<Guid, bool> moves)
    {
        var picked = Doc.Files
            .Where(f => f.TemplateId is { } id && moves(id))
            .GroupBy(f => (f.Format, To: replacedBy[f.TemplateId!.Value]))
            .Where(g => !Doc.Files.Any(o => o.Format == g.Key.Format && o.TemplateId == g.Key.To))
            .Select(g => (File: g.OrderByDescending(f => f.CreatedAt)
                .ThenByDescending(f => f.Id.ToString(), StringComparer.Ordinal).First(), g.Key.To))
            .ToList();
        foreach (var (file, to) in picked) file.RetargetTemplate(to);
        return picked.Count > 0;
    }

    /// <summary>
    /// Переопределения с перенесёнными ключами — или null, если переносить нечего. Правило то же, что
    /// у SQL миграции TemplateVersionIntegrity: ключ, который не переносится, сильнее перенесённого; из
    /// двух перенесённых в один остаётся больший по тексту ключ.
    /// </summary>
    private string? RepinParamKeys(Func<Guid, Guid?> target)
    {
        if (string.IsNullOrWhiteSpace(Doc.TemplateParams)) return null;
        JsonObject? obj;
        try { obj = JsonNode.Parse(Doc.TemplateParams) as JsonObject; }
        catch (JsonException) { return null; }
        if (obj is null) return null;

        var entries = obj.Select(kv => (kv.Key, kv.Value,
            To: Guid.TryParse(kv.Key, out var id) ? target(id) : null)).ToList();
        if (entries.All(e => e.To is null)) return null;
        var result = new JsonObject();
        foreach (var e in entries
                     .OrderBy(e => e.To is null)
                     .ThenBy(e => e.Key, StringComparer.Ordinal))
            result[e.To?.ToString() ?? e.Key] = e.Value?.DeepClone();
        return result.ToJsonString();
    }

    /// <summary>Убирает переопределения параметров удаляемого шаблона (issue #1960): ключ шаблона, которого
    /// нет, генерация не читает, а клиент пересылал бы его с каждой записью. Пустой объект → null.
    /// Вывод не меняется — в черновик не сбрасывает. Возвращает, было ли что убирать.</summary>
    public bool DropTemplateParams(Guid templateId)
    {
        if (string.IsNullOrWhiteSpace(Doc.TemplateParams)) return false;
        if (TemplateRefJson.TryParseObject(Doc.TemplateParams) is not { } root) return false;
        var keys = root.Where(p => TemplateRefJson.TemplateKey(p.Key) == templateId).Select(p => p.Key).ToList();
        if (keys.Count == 0) return false;
        foreach (var key in keys) root.Remove(key);
        Doc.TemplateParams = root.Count == 0 ? null : root.ToJsonString();
        TouchUpdatedAt();
        return true;
    }

    /// <summary>Убирает ссылки на шаблоны вне <paramref name="known"/> — шаблонов ТИПА документа
    /// (issue #1960): одиночный пин, элементы выбора и ключи переопределений. Ссылка — id в виде «D»
    /// (<see cref="TemplateRefJson.TemplateKey(string?)"/>); число, null, иной вид — не ссылка.
    /// Оставшееся пишется каноническим видом, выбор — без повторов; не массив и не объект — null,
    /// пустое — null. Чего уборка не коснулась, не трогается. Для мест, где отказать некому, —
    /// восстановления копии; то же правило у SQL миграции DocumentTemplateRefsCleanup.
    /// Возвращает число убранных ссылок.</summary>
    public int ForgetUnknownTemplates(IReadOnlySet<Guid> known)
    {
        var removed = 0;
        if (Doc.TemplateId is { } single && !known.Contains(single)) { Doc.TemplateId = null; removed++; }
        if (!string.IsNullOrWhiteSpace(Doc.TemplateIds))
        {
            var ids = TemplateRefJson.TryParseArray(Doc.TemplateIds)?.Select(TemplateRefJson.TemplateKey).ToList();
            var lost = ids is null ? 1 : ids.Count(id => id is not { } v || !known.Contains(v));
            if (lost > 0)
            {
                var kept = ids?.OfType<Guid>().Where(known.Contains).Distinct().ToList() ?? [];
                Doc.TemplateIds = kept.Count == 0 ? null : JsonSerializer.Serialize(kept);
                removed += lost;
            }
        }
        if (!string.IsNullOrWhiteSpace(Doc.TemplateParams))
        {
            if (TemplateRefJson.TryParseObject(Doc.TemplateParams) is not { } root) { Doc.TemplateParams = null; removed++; }
            else
            {
                var lost = 0;
                var renamed = false;
                var kept = new JsonObject();
                foreach (var (key, value) in root)
                {
                    if (TemplateRefJson.TemplateKey(key) is { } id && known.Contains(id))
                    {
                        renamed |= key != id.ToString();
                        kept[id.ToString()] = value?.DeepClone();
                    }
                    else lost++;
                }
                if (lost > 0 || renamed) Doc.TemplateParams = kept.Count == 0 ? null : kept.ToJsonString();
                if (renamed && lost == 0) TouchUpdatedAt(); // ключ переписан в канонический вид, убирать нечего
                removed += lost;
            }
        }
        if (removed > 0) TouchUpdatedAt();
        return removed;
    }
    public void SetTemplateParams(string? json) { Doc.TemplateParams = json; TouchUpdatedAt(); }
    public void UpdatePluginData(JsonDocument pluginData) { Doc.PluginData = pluginData; TouchUpdatedAt(); }

    public GeneratedFile AddGeneratedFile(OutputFormat format, string blobPath, Guid? templateId = null)
    {
        var file = GeneratedFile.Create(Id, format, blobPath, templateId);
        // Один файл на пару (формат, шаблон); повторная генерация тем же шаблоном заменяет его файл.
        Doc.Files.RemoveAll(f => f.Format == format && f.TemplateId == templateId);
        Doc.Files.Add(file);
        Doc.Status = DocumentStatus.Generated;
        TouchUpdatedAt();
        return file;
    }

    /// <summary>Сбрасывает документ в черновик, возвращает blob-пути удалённых файлов.</summary>
    public IReadOnlyList<string> ResetToDraft()
    {
        if (Doc.Status == DocumentStatus.Draft) return [];
        var paths = Doc.Files.Select(f => f.BlobPath).ToList();
        Doc.Files.Clear();
        Doc.Status = DocumentStatus.Draft;
        TouchUpdatedAt();
        return paths;
    }

    private static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// Убираем пустые/дублирующиеся алиасы (без учёта регистра), сохраняя порядок.
    private static List<string> NormalizeAliases(IReadOnlyList<string>? aliases)
    {
        if (aliases is null) return [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var a in aliases)
        {
            var t = a?.Trim();
            if (!string.IsNullOrEmpty(t) && seen.Add(t)) result.Add(t);
        }
        return result;
    }
}
