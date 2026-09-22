using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using MediatR;

namespace BHS.CRG.Application.Documents;

/// <summary>Системное поле в объявлении модуля — то же, что <c>ModuleSystemField</c>, но в словах ядра.</summary>
public sealed record ModuleFieldSpec(
    string Key, string Title, string Type, string? TypeId = null,
    IReadOnlyList<string>? Tags = null, bool Required = false)
{
    public IReadOnlyList<string> Tags { get; init; } = Tags ?? [];
}

/// <summary>Объявление типа модуля в словах ядра. Переводит объявление слой Api — он один знает и модули, и ядро.</summary>
public sealed record ModuleTypeSpec(
    string Module, string Code, string Name, SchemaEditLevel Level,
    IReadOnlyList<ModuleFieldSpec> Fields, string? Group = null, DocumentTypeKind Kind = DocumentTypeKind.Document);

/// <summary>
/// Проекция объявленного модулем типа в схему (ТЗ CORE-20.1, CORE-20.2, issue #958).
///
/// <para><b>Зачем проекция, а не ветка в расчёте эффективных полей.</b> Схему типа читают НАПРЯМУЮ
/// девять мест на сервере, мимо <see cref="DocumentTypeSchemaReader"/>, — и среди них реестр тэгов
/// (<see cref="SchemaTags"/>), на котором стоят печать, метаданные генерации и ключ идентичности
/// материала. Подмешивай мы системные поля веткой резолвера, тэг на системном поле не нашёлся бы, и
/// «печать теряет поле» случилось бы не в тесте, а у заказчика. Положив их в схему обычными полями
/// с метками <c>origin: module</c> и <c>locked: true</c>, мы получаем тэги, печать, ссылки, охрану
/// записи и аудит без единой правки в них.</para>
///
/// <para><b>Идемпотентность.</b> Вызывается при КАЖДОМ старте. Повторный запуск не плодит полей и
/// не затирает работу администратора: его поля остаются как есть, а у системного поля сохраняется
/// его ПОДПИСЬ — единственное, что ему оставлено даже в закрытом типе. Затирай проекция подпись,
/// правка админа молча отменялась бы при первом же перезапуске.</para>
///
/// <para><b>Почему это не обход F2.</b> <see cref="SchemaEditPolicy"/> стережёт редактор
/// АДМИНИСТРАТОРА. Здесь пишет модуль — второй законный автор схемы, и ему политика администратора
/// не указ (иначе он не смог бы убрать собственное поле: политика запрещает исчезновение поля
/// модуля). ⚠️ Поэтому у этой команды не должно появиться ни одного адреса: попав в HTTP, она
/// становится дырой в F2 целиком. Стережёт <c>ModuleTypeProjectionTests</c>.</para>
///
/// <para><b>Отказ старта.</b> Расхождение объявления с тем, что лежит в базе, останавливает
/// приложение и называет, что именно не сошлось (решение владельца 22.09.2026, тот же приём, что у
/// прав модуля и у миграции справочников). Тихая версия этого отказа — печать, печатающая
/// пустоту, и искать её будут долго.</para>
/// </summary>
public sealed record ProjectModuleTypeCommand(ModuleTypeSpec Spec) : IRequest<DocumentType>;

public sealed class ModuleTypeProjectionHandler(IRepository<DocumentType> repo)
    : IRequestHandler<ProjectModuleTypeCommand, DocumentType>
{
    public async Task<DocumentType> Handle(ProjectModuleTypeCommand cmd, CancellationToken ct)
    {
        var spec = cmd.Spec;
        Validate(spec);

        var all = await repo.GetAllAsync(ct);
        var type = all.FirstOrDefault(t => string.Equals(t.Code, spec.Code, StringComparison.Ordinal));

        if (type is null)
        {
            type = DocumentType.Create(spec.Name, spec.Code, spec.Kind, null,
                JsonDocument.Parse(Schema(spec, existing: null).ToJsonString()),
                spec.Module, TypeVisibility.Shared, editLevel: spec.Level);
            type.SetGroup(spec.Group);
            await repo.AddAsync(type, ct);
            await repo.SaveChangesAsync(ct);
            return type;
        }

        type.UpdateSchema(JsonDocument.Parse(Schema(spec, type.Schema).ToJsonString()));
        // Уровень и владелец — свойства модуля, и он их подтверждает каждым стартом. Название и
        // группу НЕ трогаем после создания: их правит администратор, и возвращать их объявлением
        // значило бы отменять его работу молча.
        type.SetLevel(spec.Level);
        type.SetOwner(spec.Module);
        repo.Update(type);
        await repo.SaveChangesAsync(ct);
        return type;
    }

    /// <summary>
    /// Новая схема: системные поля по объявлению — первыми, поля заказчика — следом, как лежали.
    /// Остальное в схеме (группы, исключения, переопределения, тэги типа, справка) не трогаем: это
    /// работа администратора, и проекция о ней ничего не знает.
    /// </summary>
    private static JsonObject Schema(ModuleTypeSpec spec, JsonDocument? existing)
    {
        var root = existing is null
            ? new JsonObject()
            : JsonNode.Parse(existing.RootElement.GetRawText()) as JsonObject ?? new JsonObject();

        var wasFields = root["fields"] as JsonArray ?? [];
        var customer = new List<JsonNode?>();
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in wasFields)
        {
            if (f is not JsonObject obj) continue;
            var key = obj["key"]?.GetValue<string>() ?? "";
            var fromModule = obj[SchemaFieldOrigin.Property]?.GetValue<string>() == SchemaFieldOrigin.Module;
            if (!fromModule)
            {
                // Поле заказчика с тем же ключом, что у системного, — то самое расхождение, ради
                // которого старт и останавливается: обе записи попали бы в схему, и кто из них
                // достанется коду модуля, решал бы порядок.
                if (spec.Fields.Any(sf => string.Equals(sf.Key, key, StringComparison.Ordinal)))
                    throw new ConflictException(
                        $"В типе «{spec.Code}» уже есть поле заказчика «{key}», а модуль " +
                        $"«{spec.Module}» объявляет системное поле с тем же ключом. Переименуйте поле " +
                        "заказчика — иначе непонятно, из какого из двух код модуля читает значение.");
                customer.Add(obj.DeepClone());
                continue;
            }
            // Подпись системного поля администратор правит законно — она переживает проекцию.
            if (obj["title"]?.GetValue<string>() is { Length: > 0 } title) titles[key] = title;
        }

        var fields = new JsonArray();
        foreach (var f in spec.Fields) fields.Add(Field(f, titles.GetValueOrDefault(f.Key)));
        foreach (var c in customer) fields.Add(c);

        root["fields"] = fields;
        return root;
    }

    private static JsonObject Field(ModuleFieldSpec f, string? adminTitle)
    {
        var node = new JsonObject
        {
            ["key"] = f.Key,
            ["title"] = adminTitle ?? f.Title,
            ["type"] = f.Type,
            [SchemaFieldOrigin.Property] = SchemaFieldOrigin.Module,
            [SchemaFieldLock.Property] = true,
        };
        if (!string.IsNullOrWhiteSpace(f.TypeId)) node["typeId"] = f.TypeId;
        if (f.Required) node["required"] = true;
        if (f.Tags.Count > 0) node["tags"] = new JsonArray([.. f.Tags.Select(t => (JsonNode?)t)]);
        return node;
    }

    /// <summary>
    /// Что проверяется ДО записи. Каждая строка — про то, что иначе сломается молча и поздно.
    /// </summary>
    private static void Validate(ModuleTypeSpec spec)
    {
        var duplicates = spec.Fields.GroupBy(f => f.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            throw new ConflictException(
                $"Модуль «{spec.Module}» объявил тип «{spec.Code}» с повторяющимися полями: " +
                $"{string.Join(", ", duplicates)}. Второе объявление молча вытеснило бы первое.");

        foreach (var f in spec.Fields)
            foreach (var tag in f.Tags)
                if (TagRegistry.Find(Domain.Schema.TagCode.CodeOf(tag)) is null)
                    throw new ConflictException(
                        $"Модуль «{spec.Module}» поставил полю «{f.Key}» типа «{spec.Code}» неизвестный " +
                        $"тэг «{tag}». Тэгом код находит поле — с опечаткой печать не нашла бы его никогда.");
    }
}
