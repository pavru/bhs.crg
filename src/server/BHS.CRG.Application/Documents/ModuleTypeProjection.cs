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
    string Key, string Title, string Type,
    IReadOnlyList<string>? Tags = null, bool Required = false, bool Locked = true)
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
/// с метками <c>origin</c> и <c>locked</c>, мы получаем тэги, печать, ссылки, охрану записи и аудит
/// без единой правки в них.</para>
///
/// <para><b>Идемпотентность.</b> Вызывается при КАЖДОМ старте. Повторный запуск не плодит полей и
/// не затирает работу администратора: его поля остаются как есть, а объявленное кладётся ПОВЕРХ
/// того, что лежит, — не пересобирая поле поимённо. Подпись системного поля, правленная
/// администратором, переживает проекцию (иначе его правка молча отменялась бы первым же
/// перезапуском), а подпись, которой он не касался, обновляется новой версией модуля.</para>
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

public sealed class ModuleTypeProjectionHandler(IRepository<DocumentType> repo, TagCatalog tags)
    : IRequestHandler<ProjectModuleTypeCommand, DocumentType>
{
    /// <summary>
    /// Виды значения, которым не нужна ссылка на другой тип, — единственные, что модуль вправе
    /// объявить. Белый список, а не чёрный: новый вид поля появится однажды в другом файле, и
    /// «чего нет в списке — отказ» встретит его вопросом, а список запретов — молчанием.
    /// </summary>
    private static readonly HashSet<string> SelfContainedKinds = new(StringComparer.Ordinal)
        { "string", "text", "number", "date", "boolean", "image", "file" };

    public async Task<DocumentType> Handle(ProjectModuleTypeCommand cmd, CancellationToken ct)
    {
        var spec = cmd.Spec;
        Validate(spec, tags);

        var all = await repo.GetAllAsync(ct);
        // Код ищем БЕЗ учёта регистра — именно так его стережёт от повторов редактор типов
        // (EnsureUnique). Сверяй мы посимвольно, «work» в базе и «WORK» в объявлении разошлись бы:
        // проекция завела бы второй тип, а администратор после этого не сохранил бы ни одного из
        // двух — уникальность кода запрещала бы оба.
        var type = all.FirstOrDefault(t => string.Equals(t.Code, spec.Code, StringComparison.OrdinalIgnoreCase));
        if (type is not null) EnsureOwnedByModule(type, spec);

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
        var system = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
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
            system[key] = (JsonObject)obj.DeepClone()!;
        }

        var fields = new JsonArray();
        foreach (var f in spec.Fields) fields.Add(Field(f, system.GetValueOrDefault(f.Key)));
        foreach (var c in customer) fields.Add(c);

        root["fields"] = fields;
        return root;
    }

    /// <summary>
    /// Поле модуля = то, что лежит, ПОВЕРХ которого положено объявленное.
    ///
    /// <para>⚠️ Не пересборка поимённо, и это главное здесь. Пересборка сохраняет ровно те свойства,
    /// которые я перечислил, и молча стирает все остальные — а политика правки разрешает
    /// администратору кое-что добавлять и на поле модуля: на уровне «расширяемый» он ВПРАВЕ дописать
    /// варианты перечисления (убирать нельзя — прежние записи на них ссылаются). Стирались бы они
    /// каждым стартом, мимо политики и без единого отказа, а записи ссылались бы в пустоту. Тот же
    /// класс ошибки стоил дефекта в #1004 и ещё одного в #1008; лечится он не памятью, а тем, что
    /// умолчание здесь — «сохранить», а не «забыть».</para>
    /// </summary>
    private static JsonObject Field(ModuleFieldSpec f, JsonObject? existing)
    {
        var node = existing ?? [];

        node["key"] = f.Key;
        node["type"] = f.Type;
        node[SchemaFieldOrigin.Property] = SchemaFieldOrigin.Module;
        node[SchemaFieldLock.Property] = f.Locked;
        node["title"] = MergedTitle(f, node);
        node[SchemaFieldModuleTitle.Property] = f.Title;

        // Обязательность и тэги ведёт модуль: чего он больше не объявляет, того быть не должно —
        // иначе снятый тэг оставался бы в базе и код продолжал бы находить по нему поле.
        if (f.Required) node["required"] = true; else node.Remove("required");
        if (f.Tags.Count > 0) node["tags"] = new JsonArray([.. f.Tags.Select(t => (JsonNode?)t)]);
        else node.Remove("tags");

        return node;
    }

    /// <summary>
    /// Чья подпись победит. Правка администратора остаётся; нетронутая подпись обновляется
    /// объявлением. Различает их <see cref="SchemaFieldModuleTitle"/> — подпись, какой её положила
    /// прошлая проекция.
    /// </summary>
    private static string MergedTitle(ModuleFieldSpec f, JsonObject node)
    {
        if (node["title"]?.GetValue<string>() is not { Length: > 0 } shown) return f.Title;

        // Метки нет — поле легло до того, как она появилась. Отличить правку человека от прошлого
        // объявления уже нечем, и выбор здесь в пользу человека: его работа дороже одной подписи.
        // Метку ставим сейчас, и со следующего старта поле живёт по общему правилу.
        if (node[SchemaFieldModuleTitle.Property]?.GetValue<string>() is not { } declared) return shown;

        return string.Equals(shown, declared, StringComparison.Ordinal) ? f.Title : shown;
    }

    /// <summary>
    /// Тип с этим кодом уже есть — но он должен принадлежать ТОМУ ЖЕ модулю.
    ///
    /// <para>Без этой проверки совпадение кода означало бы тихий захват: тип администратора (или
    /// чужого модуля) получал бы владельца и уровень правки от нас, мимо <c>TypeOwnershipRules</c>,
    /// которые стоят на всех остальных путях записи. Дальше — по цепочке: администратор теряет
    /// правку собственного типа, зависимый тип ядра нарушает ТЗ CORE-30 и перестаёт сохраняться, а
    /// при выключении модуля тип остаётся без родителя. Совпадение кода — случайность, и разнимать
    /// её должен человек, зная оба типа.</para>
    /// </summary>
    private static void EnsureOwnedByModule(DocumentType type, ModuleTypeSpec spec)
    {
        if (string.Equals(type.Module, spec.Module, StringComparison.OrdinalIgnoreCase)) return;

        throw new ConflictException(
            $"Модуль «{spec.Module}» объявляет тип с кодом «{spec.Code}», а тип с таким кодом уже " +
            $"есть и принадлежит {TypeOwnershipRules.OwnerWords(type.Module)} — это «{type.Name}». " +
            "Проекция его не забирает: переименуйте код одного из двух. Иначе модуль стал бы вести " +
            "чужой тип, а его владелец молча потерял бы право его править.");
    }

    /// <summary>
    /// Что проверяется ДО записи. Каждая строка — про то, что иначе сломается молча и поздно.
    /// </summary>
    private static void Validate(ModuleTypeSpec spec, TagCatalog tags)
    {
        foreach (var f in spec.Fields)
            if (!SelfContainedKinds.Contains(f.Type))
                throw new ConflictException(
                    $"Модуль «{spec.Module}» объявил полю «{f.Key}» типа «{spec.Code}» вид " +
                    $"«{f.Type}». Так нельзя: этот вид адресует цель — составной тип, перечисление, " +
                    "примитив или документ — по Guid, а Guid в каждой установке свой, и в коде " +
                    "модуля его нет. Поле осталось бы без цели: не нарисовалось бы и не заполнилось. " +
                    $"Допустимые виды: {string.Join(", ", SelfContainedKinds.Order(StringComparer.Ordinal))}.");

        var duplicates = spec.Fields.GroupBy(f => f.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            throw new ConflictException(
                $"Модуль «{spec.Module}» объявил тип «{spec.Code}» с повторяющимися полями: " +
                $"{string.Join(", ", duplicates)}. Второе объявление молча вытеснило бы первое.");

        foreach (var f in spec.Fields)
            foreach (var tag in f.Tags)
                if (tags.Find(Domain.Schema.TagCode.CodeOf(tag)) is null)
                    throw new ConflictException(
                        $"Модуль «{spec.Module}» поставил полю «{f.Key}» типа «{spec.Code}» неизвестный " +
                        $"тэг «{tag}». Тэгом код находит поле — с опечаткой печать не нашла бы его никогда.");
    }
}
