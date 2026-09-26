using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using MediatR;

namespace BHS.CRG.Application.Documents;

/// <summary>Системное поле в объявлении модуля — то же, что <c>ModuleSystemField</c>, но в словах ядра.</summary>
/// <param name="Target">
/// КОД типа-цели для поля, которое на другой тип ссылается (issue #963), — или <c>null</c> у поля,
/// которое хранит значение само.
///
/// <para>По коду, а не по <c>Guid</c>, и по той же причине, что у <see cref="ModuleTypeSpec.Parent" />:
/// идентификатор в каждой установке свой, а код постоянен (ТЗ TYPE-6). Без этого справочник видов
/// работ нельзя было объявить вовсе: единица измерения у него обязательна по ТЗ CORE-8, а
/// объявить её кодом было нечем — и она осталась бы ручной работой администратора, без единого
/// сторожа.</para>
///
/// <para>⚠️ Принимает его пока ОДИН вид поля — <c>complex</c> (составной тип). Массив, ссылка на
/// документ и перечисление ждут первого потребителя: <c>enum</c> и <c>primitive</c> вообще целятся
/// в другие справочники (<c>EnumType</c>, <c>PrimitiveType</c>), то есть требуют не строчки, а
/// своего разрешения цели.</para>
/// </param>
public sealed record ModuleFieldSpec(
    string Key, string Title, string Type,
    IReadOnlyList<string>? Tags = null, bool Required = false, bool Locked = true,
    string? Target = null)
{
    public IReadOnlyList<string> Tags { get; init; } = Tags ?? [];
}

/// <summary>
/// Объявление типа модуля в словах ядра. Переводит объявление слой Api — он один знает и модули, и ядро.
/// </summary>
/// <param name="Parent">
/// КОД родительского типа — или <c>null</c>, если тип ничего не наследует (issue #962).
///
/// <para>По коду, а не по <c>Guid</c>: идентификатор в каждой установке свой, и в объявлении его
/// нет — та же причина, по которой в скелете запрещены виды полей, адресующие цель. Родителя
/// обязана разрешать ТЗ CORE-30: опора — ядро или свой владелец, иначе при выключенном модуле тип
/// остался бы без родителя, то есть неописуемым, хотя сам никуда не делся.</para>
///
/// <para>⚠️ Модулям это пока НЕ отдано: <c>ModuleRecordType</c> родителя не объявляет, потому что
/// сегодня наследник нужен одному ядру («Сотрудник» производен от «Персоны», ТЗ CORE-7/TYPE-7.1).
/// Понадобится модулю — вернётся туда тем же приёмом, по коду цели и с той же проверкой. Поле с
/// выбором, у которого нет ни одного пользователя, — обещание, которое некому исполнить.</para>
/// </param>
/// <param name="SkipWhenBlocked">
/// Что делать, если тип ЗАВЕСТИ НЕЛЬЗЯ, а его ещё нет: <c>false</c> (умолчание) — отказ старта,
/// <c>true</c> — тип не заводится, и об этом пишется в журнал запуска.
///
/// <para>Завести нельзя по двум причинам: нет объявленного родителя или занято имя.</para>
///
/// <para>Нужно ядру и только ему, и разница с модулем существенна. Модуль включает
/// администратор — отказ старта для него действие: выключить модуль и разобраться. Ядро выключить
/// нельзя, поэтому его отказ — это неподнимаемая установка, а совет «переименуйте тип» требует
/// работающего приложения (ревью PR #1052). Пропуск же не портит ничего: справочник просто не
/// появляется, пока условия не сложатся.</para>
///
/// <para>Справочник сотрудников производен от «Персоны», а «Персону» ядро НЕ заводит: она
/// существует у заказчика потому, что её когда-то завёл человек. На чистой установке справочников
/// нет ни одного — и отказ старта означал бы, что новая установка не поднимается вовсе (поймано
/// тестом; живая проверка на копии рабочей базы этого показать не может в принципе — там
/// «Персона» есть).</para>
///
/// <para>⚠️ Послабление касается ТОЛЬКО случая «ни родителя, ни наследника». Если тип уже заведён,
/// а родитель пропал — это отказ при любом значении: существующий «Сотрудник» остался бы без ФИО,
/// и заметили бы это не здесь.</para>
/// </param>
public sealed record ModuleTypeSpec(
    string Module, string Code, string Name, SchemaEditLevel Level,
    IReadOnlyList<ModuleFieldSpec> Fields, string? Group = null, DocumentTypeKind Kind = DocumentTypeKind.Document,
    string? Parent = null, bool SkipWhenBlocked = false);

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
public sealed record ProjectModuleTypeCommand(ModuleTypeSpec Spec) : IRequest<DocumentType?>;

public sealed class ModuleTypeProjectionHandler(IRepository<DocumentType> repo, TagCatalog tags)
    : IRequestHandler<ProjectModuleTypeCommand, DocumentType?>
{
    /// <summary>
    /// Виды значения, которым не нужна ссылка на другой тип, — единственные, что модуль вправе
    /// объявить. Белый список, а не чёрный: новый вид поля появится однажды в другом файле, и
    /// «чего нет в списке — отказ» встретит его вопросом, а список запретов — молчанием.
    /// </summary>
    private static readonly HashSet<string> SelfContainedKinds = new(StringComparer.Ordinal)
        { "string", "text", "number", "date", "boolean", "image", "file" };

    /// <summary>
    /// Виды значения, которые цель ПРИНИМАЮТ — по коду типа (<see cref="ModuleFieldSpec.Target" />).
    /// Список отдельный и короткий нарочно: каждый вид тут требует своей проверки цели (род, чей
    /// он, тот ли справочник), и вид без потребителя означал бы непроверенную проверку.
    /// </summary>
    private static readonly HashSet<string> TargetedKinds = new(StringComparer.Ordinal) { "complex" };

    public async Task<DocumentType?> Handle(ProjectModuleTypeCommand cmd, CancellationToken ct)
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

        var parentId = ResolveParent(spec, all, typeExists: type is not null);
        // Родителя нет, наследника тоже — заводить нечего (см. SkipWhenBlocked).
        if (parentId is null && spec.Parent is { Length: > 0 } && type is null) return null;

        // Цели полей — тем же порядком и с тем же послаблением: нет типа единицы измерения, нет и
        // классификатора, который её требует.
        if (ResolveTargets(spec, all, typeExists: type is not null) is not { } targets) return null;

        if (type is null)
        {
            if (NameTakenBy(spec, all) is { } taken)
            {
                if (spec.SkipWhenBlocked) return null;
                throw NameTakenRefusal(spec, taken);
            }

            type = DocumentType.Create(spec.Name, spec.Code, spec.Kind, parentId,
                JsonDocument.Parse(Schema(spec, existing: null, targets).ToJsonString()),
                spec.Module, TypeVisibility.Shared, editLevel: spec.Level);
            type.SetGroup(spec.Group);
            EnsureCardinalityHolds(type, all, spec);
            await repo.AddAsync(type, ct);
            await repo.SaveChangesAsync(ct);
            return type;
        }

        var projected = JsonDocument.Parse(Schema(spec, type.Schema, targets).ToJsonString());
        // Родителя трогаем ТОЛЬКО если объявление о нём говорит (ревью PR #1052). Иначе каждый
        // старт обнулял бы родителя у всех типов модулей: объявление модуля родителя не несёт, а
        // администратор его ставит — это штатное действие (тип модуля вправе опереться на тип ядра,
        // ТЗ CORE-30). Тип молча терял бы унаследованные поля, и виновником выглядел бы кто угодно,
        // кроме перезапуска.
        //
        // Ставим ДО проверки кратности: она считает по цепочке наследования, и посчитанная по
        // прежней цепочке сказала бы про набор полей, которого уже не будет.
        if (spec.Parent is { Length: > 0 }) type.SetParent(parentId);
        EnsureCardinalityHolds(type.WithSchema(projected), all, spec);
        type.UpdateSchema(projected);
        // Уровень и владелец — свойства модуля, и он их подтверждает каждым стартом.
        // Название и группу НЕ трогаем после создания: их правит администратор, и возвращать их
        // объявлением значило бы отменять его работу молча.
        type.SetLevel(spec.Level);
        type.SetOwner(spec.Module);
        repo.Update(type);
        await repo.SaveChangesAsync(ct);
        return type;
    }

    /// <summary>
    /// Кратность тэгов после проекции (ТЗ TYPE-21, issue #959; ревью PR #1012).
    ///
    /// <para>Объявление модуля ставит тэги на свои поля, а рядом в том же типе (и в его потомках)
    /// уже могут стоять поля заказчика с теми же тэгами. Пропусти мы это — модуль завёл бы тип,
    /// который администратор не сможет сохранить НИКОГДА, а на уровнях «закрытый» и «расширяемый»
    /// не сможет и починить: снимать тэг с поля модуля ему не дадут.</para>
    ///
    /// <para>Отказ останавливает старт, как и прочие расхождения объявления с базой: тихо это
    /// значило бы оставить систему с типом, в котором код модуля найдёт не то поле.</para>
    /// </summary>
    private void EnsureCardinalityHolds(DocumentType projected, IReadOnlyList<DocumentType> all, ModuleTypeSpec spec)
    {
        var violations = TagCardinalityValidator.Validate(tags, projected, all);
        if (violations.Count == 0) return;
        throw new ConflictException(
            $"Объявление, которое делает {Who(spec)}, нарушает кратность тэгов в типе «{spec.Code}»: " +
            string.Join(" ", violations.Select(v => v.Describe())) +
            " Снимите тэг с поля заказчика или уберите его из объявления модуля: " +
            "иначе тип нельзя будет сохранить из редактора.");
    }

    /// <summary>
    /// Новая схема: системные поля по объявлению — первыми, поля заказчика — следом, как лежали.
    /// Остальное в схеме (группы, исключения, переопределения, тэги типа, справка) не трогаем: это
    /// работа администратора, и проекция о ней ничего не знает.
    /// </summary>
    private static JsonObject Schema(
        ModuleTypeSpec spec, JsonDocument? existing, IReadOnlyDictionary<string, Guid> targets)
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
                        $"В типе «{spec.Code}» уже есть поле заказчика «{key}», а {Who(spec)} " +
                        "объявляет системное поле с тем же ключом. Переименуйте поле " +
                        "заказчика — иначе непонятно, из какого из двух код модуля читает значение.");
                customer.Add(obj.DeepClone());
                continue;
            }
            system[key] = (JsonObject)obj.DeepClone()!;
        }

        var fields = new JsonArray();
        foreach (var f in spec.Fields) fields.Add(Field(f, system.GetValueOrDefault(f.Key), targets));
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
    private static JsonObject Field(
        ModuleFieldSpec f, JsonObject? existing, IReadOnlyDictionary<string, Guid> targets)
    {
        var node = existing ?? [];

        node["key"] = f.Key;
        node["type"] = f.Type;

        // Цель ведёт модуль, как обязательность и тэги: перестал объявлять — ссылки быть не должно.
        // ⚠️ Идентификатор кладётся СТРОКОЙ: так его пишет редактор типов и так читает
        // SchemaFieldInfo. Положи мы его иначе — поле нарисовалось бы, а цель не нашлась.
        if (targets.TryGetValue(f.Key, out var target)) node["typeId"] = target.ToString();
        else node.Remove("typeId");

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
    /// Имя нового типа не должно быть занято (issue #962).
    ///
    /// <para>Дыра, найденная на живой базе: проекция создавала тип, минуя проверку уникальности
    /// имени, которая стоит на пути администратора (<c>EnsureUnique</c>). Последствие не в том, что
    /// имён станет два, — а в том, что ПОСЛЕ этого из редактора не сохранится НИ ОДИН из двух
    /// типов: уникальность запретит обоих. Приложение при этом поднимется, и связь между «не
    /// сохраняется тип» и «полгода назад появился модуль» никто не восстановит.</para>
    ///
    /// <para>Наступили на это с «Сотрудником»: в рабочей базе тип с кодом <c>Персона</c> носит имя
    /// «Сотрудник» (ТЗ TYPE-7.1 велит вернуть ему «Лицо» — ровно «чтобы два типа не назывались
    /// одинаково»). Проверка кода тут не спасает: коды как раз РАЗНЫЕ.</para>
    ///
    /// <para>Только при СОЗДАНИИ. У существующего типа имя ведёт администратор, и проекция его не
    /// трогает — сверять его с объявлением значило бы запрещать переименование задним числом.</para>
    /// </summary>
    public static DocumentType? NameTakenBy(ModuleTypeSpec spec, IReadOnlyList<DocumentType> all) =>
        all.FirstOrDefault(
            t => string.Equals(t.Name.Trim(), spec.Name.Trim(), StringComparison.OrdinalIgnoreCase));

    private static ConflictException NameTakenRefusal(ModuleTypeSpec spec, DocumentType taken) =>
        new($"{WhoCapitalized(spec)} заводит тип «{spec.Name}» (код «{spec.Code}»), а тип с таким " +
            $"именем уже есть — код «{taken.Code}», принадлежит {TypeOwnershipRules.OwnerWords(taken.Module)}. " +
            "Завести второй нельзя: после этого из редактора не сохранился бы ни один из двух — " +
            "уникальность имени запретила бы обоих. Переименуйте существующий тип.");

    /// <summary>
    /// Родитель по КОДУ из объявления (issue #962) — или <c>null</c>, если родителя не объявляли.
    ///
    /// <para>Отказ старта, а не тихий пропуск, обеим половинам. Нет типа с таким кодом — наследник
    /// поднялся бы без половины полей, и «Сотрудник» оказался бы без ФИО: форма нарисовалась бы,
    /// печать промолчала, а заметили бы это на выгрузке. Опора запрещена по ТЗ CORE-30 — тип ядра
    /// остался бы без родителя при выключенном модуле, то есть неописуемым, хотя сам никуда не
    /// делся; ровно это проверяет <c>EnsureParentAllowsDerived</c> на пути администратора, и
    /// проекция не вправе быть дырой мимо него.</para>
    /// </summary>
    private static Guid? ResolveParent(ModuleTypeSpec spec, IReadOnlyList<DocumentType> all, bool typeExists)
    {
        if (string.IsNullOrWhiteSpace(spec.Parent)) return null;

        var parent = all.FirstOrDefault(
            t => string.Equals(t.Code, spec.Parent, StringComparison.OrdinalIgnoreCase));

        // Ни родителя, ни наследника — чистая установка: справочников в ней нет ни одного, и
        // выводить один из другого не из чего. Заведётся сам, как только появится родитель.
        if (parent is null && !typeExists && spec.SkipWhenBlocked) return null;

        if (parent is null)
            throw new ConflictException(
                $"{WhoCapitalized(spec)} объявляет тип «{spec.Code}» производным от «{spec.Parent}», а типа с " +
                "таким кодом в системе нет. Наследник поднялся бы без унаследованных полей — с виду " +
                "целый, но без половины сведений. Проверьте код родителя в объявлении: его могли " +
                "переименовать из редактора типов.");

        if (!TypeOwnershipRules.Allows(spec.Module, parent.Module))
            throw new ConflictException(
                $"{WhoCapitalized(spec)} объявляет тип «{spec.Code}» производным от «{parent.Name}», а тот " +
                $"принадлежит {TypeOwnershipRules.OwnerWords(parent.Module)} (ТЗ CORE-30). Опора " +
                "разрешена на ядро и на своего владельца: иначе при выключенном модуле наследник " +
                "остался бы без родителя — неописуемым, хотя сам никуда не делся.");

        if (parent.Kind != spec.Kind)
            throw new ConflictException(
                $"{WhoCapitalized(spec)} объявляет тип «{spec.Code}» ({spec.Kind}) производным от «{parent.Name}» " +
                $"({parent.Kind}). Наследование идёт внутри одного рода: род решает, чем объект " +
                "является, и сменить его через родителя значило бы описать одну сущность дважды.");

        return parent.Id;
    }

    /// <summary>
    /// Цели полей по КОДАМ из объявления (issue #963): «ключ поля → идентификатор типа-цели».
    /// <c>null</c> — тип заводить нельзя: цели нет, а послабление разрешено (см. SkipWhenBlocked).
    ///
    /// <para>Отказы — те же три, что у родителя, и по тем же причинам. Нет типа с таким кодом:
    /// поле осталось бы без цели, то есть не нарисовалось бы и не заполнилось, а справочник с виду
    /// был бы целым. Цель принадлежит чужому модулю: при его выключении ссылка повисла бы, и
    /// сильнее всего это бьёт по ядру — оно выключению не подлежит, а половина его справочника
    /// уехала бы вместе с модулем (ТЗ CORE-30). Не тот род: составное поле умеет показывать только
    /// составной тип, документ в нём не нарисуется.</para>
    /// </summary>
    private static Dictionary<string, Guid>? ResolveTargets(
        ModuleTypeSpec spec, IReadOnlyList<DocumentType> all, bool typeExists)
    {
        var resolved = new Dictionary<string, Guid>(StringComparer.Ordinal);

        foreach (var f in spec.Fields)
        {
            if (f.Target is not { Length: > 0 } code) continue;

            var target = all.FirstOrDefault(
                t => string.Equals(t.Code, code, StringComparison.OrdinalIgnoreCase));

            // Цели нет, наследника тоже — чистая установка: справочников в ней нет ни одного.
            if (target is null && !typeExists && spec.SkipWhenBlocked) return null;

            if (target is null)
                throw new ConflictException(
                    $"{WhoCapitalized(spec)} объявляет полю «{f.Key}» типа «{spec.Code}» ссылку на тип " +
                    $"«{code}», а типа с таким кодом в системе нет. Поле осталось бы без цели: не " +
                    "нарисовалось бы и не заполнилось, а справочник с виду был бы целым. Проверьте " +
                    "код цели в объявлении — его могли переименовать из редактора типов.");

            if (!TypeOwnershipRules.Allows(spec.Module, target.Module))
                throw new ConflictException(
                    $"{WhoCapitalized(spec)} объявляет полю «{f.Key}» типа «{spec.Code}» ссылку на " +
                    $"«{target.Name}», а тот принадлежит {TypeOwnershipRules.OwnerWords(target.Module)} " +
                    "(ТЗ CORE-30). Ссылаться разрешено на ядро и на своего владельца: иначе при " +
                    "выключенном модуле поле указывало бы в пустоту.");

            if (target.Kind != DocumentTypeKind.Composite)
                throw new ConflictException(
                    $"{WhoCapitalized(spec)} объявляет полю «{f.Key}» типа «{spec.Code}» ссылку на " +
                    $"«{target.Name}», а это {target.Kind}, не составной тип. Составное поле умеет " +
                    "показывать только составной тип — документ в нём не нарисуется.");

            resolved[f.Key] = target.Id;
        }

        return resolved;
    }

    /// <summary>
    /// Код цели, которой в системе нет, — для журнала запуска (см. <c>ModuleTypeProjector</c>).
    /// Спрашивается ЗАНОВО по базе: причину пропуска называют фактами, а не памятью о том, почему
    /// команда вернула «не завёл».
    /// </summary>
    public static string? MissingTargetOf(ModuleTypeSpec spec, IReadOnlyList<DocumentType> all) =>
        spec.Fields
            .Select(f => f.Target)
            .Where(code => code is { Length: > 0 })
            .FirstOrDefault(code => !all.Any(
                t => string.Equals(t.Code, code, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Кто объявил — словами. У ядра модуля нет, и «модуль «core»» было бы неправдой: с issue #962
    /// по этому же пути ходит ядро, а отказ, называющий его модулем, отправил бы читателя искать
    /// выключатель, которого не существует. Тот же приём, что у
    /// <see cref="TypeOwnershipRules.OwnerWords" />.
    ///
    /// <para>⚠️ Глаголы в этих отказах — в НАСТОЯЩЕМ времени: «ядро объявляет» и «модуль объявляет»
    /// согласуются одинаково, а «объявил/объявило» разошлись бы по роду, и одна из двух половин
    /// звучала бы безграмотно.</para>
    /// </summary>
    private static string Who(ModuleTypeSpec spec) => TypeOwner.IsCore(spec.Module)
        ? "ядро"
        : $"модуль «{spec.Module}»";

    /// <summary>То же с заглавной — для начала фразы.</summary>
    private static string WhoCapitalized(ModuleTypeSpec spec) =>
        char.ToUpperInvariant(Who(spec)[0]) + Who(spec)[1..];

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
            $"{WhoCapitalized(spec)} объявляет тип с кодом «{spec.Code}», а тип с таким кодом уже " +
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
        {
            var targeted = f.Target is { Length: > 0 };

            if (targeted && !TargetedKinds.Contains(f.Type))
                throw new ConflictException(
                    $"{WhoCapitalized(spec)} объявляет полю «{f.Key}» типа «{spec.Code}» цель " +
                    $"«{f.Target}», а вид поля «{f.Type}» цели не принимает. Ссылка была бы записана " +
                    "в схему рядом с видом, который её не читает: поле нарисовалось бы пустым. " +
                    $"Цель принимают: {string.Join(", ", TargetedKinds.Order(StringComparer.Ordinal))}.");

            if (!targeted && !SelfContainedKinds.Contains(f.Type))
                throw new ConflictException(
                    $"{WhoCapitalized(spec)} объявляет полю «{f.Key}» типа «{spec.Code}» вид " +
                    $"«{f.Type}» без цели. Так нельзя: этот вид адресует цель — составной тип, " +
                    "перечисление, примитив или документ, — а без неё поле не нарисуется и не " +
                    "заполнится. Составной тип называется КОДОМ в «Target»; перечисление и примитив " +
                    "кодом не объявляются вовсе — они целятся в другие справочники. " +
                    $"Хранят значение сами: {string.Join(", ", SelfContainedKinds.Order(StringComparer.Ordinal))}.");
        }

        var duplicates = spec.Fields.GroupBy(f => f.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            throw new ConflictException(
                $"{WhoCapitalized(spec)} объявляет тип «{spec.Code}» с повторяющимися полями: " +
                $"{string.Join(", ", duplicates)}. Второе объявление молча вытеснило бы первое.");

        foreach (var f in spec.Fields)
            foreach (var tag in f.Tags)
                if (tags.Find(Domain.Schema.TagCode.CodeOf(tag)) is null)
                    throw new ConflictException(
                        $"{WhoCapitalized(spec)} ставит полю «{f.Key}» типа «{spec.Code}» неизвестный " +
                        $"тэг «{tag}». Тэгом код находит поле — с опечаткой печать не нашла бы его никогда.");
    }
}
