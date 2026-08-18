using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Templates;
using MediatR;

namespace BHS.CRG.Application.Generation;

/// <param name="TypeName">Тип, чей блок переименован.</param>
public record BlockRenamePlan(string TypeName, string TypeCode, string OldName, string NewName);

/// <param name="Where">Что переписано: «тип X» или «шаблон Y (версия N)».</param>
public record TextChangePlan(string Where, int Calls, int Paths);

/// <param name="Applied">false — сухой прогон: ничего не записано.</param>
/// <param name="Blocked">Причины, по которым применение невозможно. Непустой список означает, что
/// запись не производилась даже при <c>dryRun=false</c>.</param>
/// <param name="Ambiguous">Места, где имя блока встречено не в позиции вызова: переписать их наугад
/// нельзя, нужна ручная правка. Это не блокирует миграцию — вызовы вокруг переписываются.</param>
/// <param name="SkippedTemplates">Неактивные версии шаблонов. Их намеренно не трогаем: откат на
/// такую версию после миграции всё равно даёт неработающий шаблон (в системе больше нет имён, к
/// которым она обращается), а переписывание двадцати исторических версий создало бы видимость,
/// будто откат поддержан.</param>
/// <param name="UserLib">Правки в Typst-библиотеке. Она тоже может звать блоки по имени, а после
/// перехода на алиасы голое имя перестаёт разрешаться — молча, потому что импорт в теле функции
/// Typst выполняет только при вызове. Сегодня таких вызовов ноль, но узнать об этом можно лишь
/// посмотрев, а не понадеявшись.</param>
/// <param name="RepinnedDocuments">Сколько документов пересажено с прежней версии шаблона на новую.
/// Без этого миграция оставляла бы систему неработающей: документ помнит ВЫБРАННУЮ версию, новая
/// версия делает прежнюю неактивной, и генерация отвечает «ни один из выбранных шаблонов не
/// активен» (проверено живьём — так упали все девять документов комплекта).</param>
public record PrefixedAddressingReport(
    bool Applied,
    IReadOnlyList<BlockRenamePlan> Renames,
    IReadOnlyList<TextChangePlan> Blocks,
    IReadOnlyList<TextChangePlan> Templates,
    IReadOnlyList<string> Ambiguous,
    IReadOnlyList<string> SkippedTemplates,
    int RepinnedDocuments,
    IReadOnlyList<TextChangePlan> UserLib,
    IReadOnlyList<string> Blocked);

/// <summary>Переход на адресацию <c>Код.Имя</c> (issue #773). <paramref name="DryRun"/> — посчитать
/// и показать, ничего не записывая.</summary>
public record MigrateToPrefixedAddressingCommand(bool DryRun) : IRequest<PrefixedAddressingReport>;

/// <summary>
/// Одноразовый перевод системы на префиксную адресацию блоков (issue #773).
///
/// <para><b>Команда, а не EF-миграция.</b> Проверка результата требует внешнего Typst CLI, а старт
/// приложения на внешний бинарник опираться не должен: не поднялся компилятор — не поднялась
/// система. Плюс это правка пользовательских текстов, и запускать её должен человек, посмотрев
/// сухой прогон.</para>
///
/// <para><b>Посчитать всё → проверить → записать один раз.</b> Однозначность переписывания держится
/// на том, что карта имён построена по ДОмиграционному снимку, где имена глобально уникальны: после
/// срезания префиксов <c>full</c> будет и у «Адреса», и у «Подписанта». Поэтому частичная запись
/// недопустима — продолжить прерванную миграцию было бы уже нечем.</para>
/// </summary>
public class MigrateToPrefixedAddressingHandler(
    IRepository<DocumentType> docTypeRepo,
    IRepository<Template> templateRepo,
    IDomainObjectRepository instanceRepo,
    IUserLibProvider userLib,
    IRepository<Domain.Documents.TypstUserLib> libRepo,
    IRepository<Domain.Documents.TypstUserLibFile> libFileRepo,
    ITypstSyntaxChecker checker
) : IRequestHandler<MigrateToPrefixedAddressingCommand, PrefixedAddressingReport>
{
    public async Task<PrefixedAddressingReport> Handle(
        MigrateToPrefixedAddressingCommand cmd, CancellationToken ct)
    {
        var types = (await docTypeRepo.GetAllAsync(ct))
            .OrderBy(t => t.Code, StringComparer.Ordinal).ToList();
        var templates = await templateRepo.GetAllAsync(ct);

        var reserved = await ReservedNamesAsync(ct);
        var renames = new List<BlockRenamePlan>();
        var blocked = new List<string>();

        // ── 1. План переименований и карта «старое имя → адрес» ──────────────────────────────
        var map = new Dictionary<string, BlockRef>(StringComparer.Ordinal);
        foreach (var t in types)
        {
            var records = TypstPreambleBuilder.ExtractRenders(t).ToList();
            if (records.Count == 0) continue;

            if (!TypstPreambleBuilder.IsTypstIdentifier(t.Code))
            {
                blocked.Add($"Код типа «{t.Code}» ({t.Name}) не годится как имя в Typst — "
                            + "переименуйте тип, иначе его блоки останутся недоступны шаблонам.");
                continue;
            }

            var shortNames = TypeBlockShortName.Shorten(records.Select(r => r.FnName).ToList(), reserved);
            foreach (var r in records)
            {
                var newName = shortNames.TryGetValue(r.FnName, out var s) ? s : r.FnName;
                if (newName != r.FnName) renames.Add(new(t.Name, t.Code, r.FnName, newName));

                // Имена ДО миграции глобально уникальны — иначе система бы не собиралась. Совпадение
                // здесь означало бы, что снимок уже переписан (повторный запуск): продолжать нельзя,
                // адрес разрешался бы наугад.
                if (!map.TryAdd(r.FnName, new BlockRef(t.Code, r.FnName, newName)))
                    blocked.Add($"Имя блока «{r.FnName}» встречается у нескольких типов — "
                                + "похоже, миграция уже выполнялась. Повторный запуск невозможен.");
            }
        }

        // ── 2. Переписывание текстов (в памяти) ──────────────────────────────────────────────
        var ambiguous = new List<string>();
        var blockChanges = new List<TextChangePlan>();
        var newSchemas = new Dictionary<Guid, JsonDocument>();

        foreach (var t in types)
        {
            var (schema, calls, paths, amb) = RewriteTypeSchema(t, map);
            // Предупреждения собираем ДО проверки «есть ли правки»: тип, которому переписывать нечего,
            // всё равно может упоминать имя блока не в позиции вызова — а это единственный случай,
            // ради которого человека и просят посмотреть глазами.
            ambiguous.AddRange(amb.Select(a => $"тип «{t.Name}»: имя «{a}» встречено не в позиции вызова"));
            if (schema is null) continue;
            newSchemas[t.Id] = schema;
            if (calls + paths > 0) blockChanges.Add(new($"тип «{t.Name}»", calls, paths));
        }

        var templateChanges = new List<TextChangePlan>();
        var newTemplateContent = new Dictionary<Guid, string>();
        var skipped = new List<string>();

        foreach (var tpl in templates)
        {
            var r = TypstCallRewriter.Rewrite(tpl.Content, map);
            if (r.Calls + r.Paths == 0) continue;
            if (!tpl.IsActive)
            {
                skipped.Add($"«{tpl.Name}» версия {tpl.Version} — историческая, вызовов: {r.Calls}");
                continue;
            }
            newTemplateContent[tpl.Id] = r.Text;
            templateChanges.Add(new($"шаблон «{tpl.Name}» (версия {tpl.Version})", r.Calls, r.Paths));
            ambiguous.AddRange(r.Ambiguous.Select(a =>
                $"шаблон «{tpl.Name}»: имя «{a}» встречено не в позиции вызова"));
        }

        // Библиотека — третий носитель вызовов, наряду со схемами и шаблонами.
        var libSnapshot = await userLib.GetAsync(ct);
        var libChanges = new List<TextChangePlan>();
        var newLibEntry = (string?)null;
        var newLibFiles = new Dictionary<string, string>(StringComparer.Ordinal);

        var libEntryRewrite = TypstCallRewriter.Rewrite(libSnapshot.Entrypoint ?? "", map);
        if (libEntryRewrite.Calls > 0)
        {
            newLibEntry = libEntryRewrite.Text;
            libChanges.Add(new("библиотека: точка входа", libEntryRewrite.Calls, 0));
        }
        foreach (var f in libSnapshot.Files)
        {
            var r = TypstCallRewriter.Rewrite(f.Content ?? "", map);
            if (r.Calls == 0) continue;
            newLibFiles[f.Path] = r.Text;
            libChanges.Add(new($"библиотека: {f.Path}", r.Calls, 0));
        }

        // ── 3. Проверка: собранные блоки обязаны компилироваться ─────────────────────────────
        var previewTypes = types.Select(t =>
            newSchemas.TryGetValue(t.Id, out var s) ? t.WithSchema(s) : t).ToList();
        var built = TypstPreambleBuilder.BuildWithDiagnostics(previewTypes);
        blocked.AddRange(built.Diagnostics
            .Where(d => d.Severity == TypstBlockDiagnosticSeverity.Error)
            .Select(d => "После переписывания: " + d.Message));

        try
        {
            foreach (var e in await checker.CheckAsync(built.Files, ct))
                blocked.Add($"После переписывания не компилируется {e.File}:{e.Line} — {e.Message}");
        }
        catch (Exception ex)
        {
            blocked.Add($"Проверка компиляцией недоступна: {ex.Message}. Без неё миграция не запускается.");
        }

        // Вызовы шаблонов сверяем с реестром адресов, а не компиляцией: у шаблона нет данных, и
        // компиляция впустую упала бы на первом же обращении к полю документа.
        var addresses = map.Values
            .Select(v => $"{v.TypeCode}.{v.NewName}").ToHashSet(StringComparer.Ordinal);
        foreach (var (id, content) in newTemplateContent)
        {
            var name = templates.First(t => t.Id == id).Name;
            foreach (var call in UnknownAddresses(content, addresses))
                blocked.Add($"Шаблон «{name}» обращается к «{call}» — такого блока нет.");
        }

        // Документы помнят ВЫБРАННЫЕ версии шаблонов; новая версия делает прежнюю неактивной, и
        // такой документ перестаёт генерироваться. Считаем пересадку заранее — чтобы сухой прогон
        // показал и её.
        //
        // Берём документы ПО ТИПАМ затронутых шаблонов, а не GetAllAsync: общий репозиторий грузит
        // объект без документной фасеты, и `IsDocument` у всех оказывается false — сухой прогон
        // молча показывал «к пересадке 0», хотя на живых данных их десять.
        var affectedTypeIds = newTemplateContent.Keys
            .Select(id => templates.First(t => t.Id == id).DocumentTypeId).Distinct();
        // Пин бывает двух видов: список TemplateIds и одиночный legacy-TemplateId. Домен считает
        // пином оба (PinsTemplate), поэтому и пересаживать надо оба — иначе документ со старым видом
        // пина упрётся ровно в тот отказ, который эта пересадка и предотвращает.
        var repin = new List<Domain.Objects.DomainObject>();
        foreach (var typeId in affectedTypeIds)
            repin.AddRange((await instanceRepo.GetDocumentsOfTypeAsync(typeId, ct))
                .Where(o => newTemplateContent.Keys.Any(o.PinsTemplate)));

        if (cmd.DryRun || blocked.Count > 0)
            return new(false, renames, blockChanges, templateChanges, ambiguous, skipped,
                repin.Count, libChanges, blocked);

        // ── 4. Запись — одной транзакцией ────────────────────────────────────────────────────
        foreach (var t in types)
            if (newSchemas.TryGetValue(t.Id, out var schema))
            {
                t.UpdateSchema(schema);
                docTypeRepo.Update(t);
            }

        var replacedBy = new Dictionary<Guid, Guid>();
        foreach (var (id, content) in newTemplateContent)
        {
            var tpl = templates.First(t => t.Id == id);
            var version = tpl.CreateNewVersion(content, "Переход на адресацию Код.Имя (issue #773)");
            templateRepo.Update(tpl);
            await templateRepo.AddAsync(version, ct);
            replacedBy[id] = version.Id;
        }

        // Пересаживаем документы на новые версии — вместе с их переопределениями параметров, иначе
        // настройки страницы и подстановки, привязанные к прежнему id, потерялись бы молча.
        foreach (var obj in repin)
        {
            foreach (var (oldId, newId) in replacedBy)
                if (obj.TemplateId == oldId) obj.SetTemplate(newId);
            obj.SetTemplateIds(ReplaceIds(obj.TemplateIds, replacedBy));
            obj.SetTemplateParams(ReplaceParamKeys(obj.TemplateParams, replacedBy));
            instanceRepo.Update(obj);
        }

        if (newLibEntry is not null)
        {
            var lib = (await libRepo.GetAllAsync(ct)).FirstOrDefault();
            if (lib is not null) { lib.UpdateContent(newLibEntry); libRepo.Update(lib); }
        }
        if (newLibFiles.Count > 0)
            foreach (var file in await libFileRepo.GetAllAsync(ct))
                if (newLibFiles.TryGetValue(file.Path, out var content))
                {
                    file.Update(content);
                    libFileRepo.Update(file);
                }

        await docTypeRepo.SaveChangesAsync(ct);
        await templateRepo.SaveChangesAsync(ct);
        await instanceRepo.SaveChangesAsync(ct);
        if (newLibEntry is not null) await libRepo.SaveChangesAsync(ct);
        if (newLibFiles.Count > 0) await libFileRepo.SaveChangesAsync(ct);

        return new(true, renames, blockChanges, templateChanges, ambiguous, skipped,
            repin.Count, libChanges, blocked);
    }

    /// <summary>Упоминает ли документ хоть одну из перечисленных версий шаблона.</summary>
    private static bool MentionsAny(string? templateIdsJson, IEnumerable<Guid> ids)
        => !string.IsNullOrWhiteSpace(templateIdsJson)
           && ids.Any(id => templateIdsJson.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Список выбранных версий: старые id → новые. Форма JSON сохраняется как есть.</summary>
    private static string? ReplaceIds(string? json, IReadOnlyDictionary<Guid, Guid> replacedBy)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;
        foreach (var (oldId, newId) in replacedBy)
            json = json.Replace(oldId.ToString(), newId.ToString(), StringComparison.OrdinalIgnoreCase);
        return json;
    }

    /// <summary>Переопределения параметров хранятся объектом «id версии → значения»: переносим ключи,
    /// иначе документ после пересадки потерял бы свои настройки, ничего об этом не сказав.</summary>
    private static string? ReplaceParamKeys(string? json, IReadOnlyDictionary<Guid, Guid> replacedBy)
        => ReplaceIds(json, replacedBy);

    /// <summary>Схема типа с переписанными блоками — или null, если менять нечего.</summary>
    private static (JsonDocument? Schema, int Calls, int Paths, List<string> Ambiguous) RewriteTypeSchema(
        DocumentType type, IReadOnlyDictionary<string, BlockRef> map)
    {
        var root = JsonNode.Parse(type.Schema.RootElement.GetRawText())?.AsObject();
        if (root?["typstRenders"] is not JsonArray renders || renders.Count == 0)
            return (null, 0, 0, []);

        int calls = 0, paths = 0;
        var ambiguous = new List<string>();
        var changed = false;

        foreach (var node in renders)
        {
            if (node is not JsonObject render) continue;
            var fnName = render["fnName"]?.GetValue<string>();
            var block = render["block"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(fnName) || string.IsNullOrWhiteSpace(block)) continue;

            var rewritten = TypstCallRewriter.Rewrite(block, map, type.Code, fixPaths: true);
            if (rewritten.Text != block) { render["block"] = rewritten.Text; changed = true; }
            calls += rewritten.Calls;
            paths += rewritten.Paths;
            ambiguous.AddRange(rewritten.Ambiguous);

            if (map.TryGetValue(fnName, out var self) && self.NewName != fnName)
            {
                render["fnName"] = self.NewName;
                changed = true;
            }
        }

        return changed ? (JsonDocument.Parse(root.ToJsonString()), calls, paths, ambiguous)
                       : (null, calls, paths, ambiguous);
    }

    /// <summary>Обращения вида <c>Код.имя(</c>, которых нет среди собранных блоков.</summary>
    private static IEnumerable<string> UnknownAddresses(string content, HashSet<string> known)
    {
        var masked = TypstTextMask.Mask(content, TypstTextMask.Keep.CodeOnly);
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(
                     masked, @"(?<![\w\-.])([\p{L}_][\w\-]*)\s*\.\s*([\p{L}_][\w\-]*)\s*\("))
        {
            var address = $"{m.Groups[1].Value}.{m.Groups[2].Value}";
            // Точечные обращения бывают не только к блокам (it.Поле, sym.space); сверяем лишь те,
            // чей префикс совпал с кодом известного типа, — остальное не наше дело.
            if (known.Any(k => k.StartsWith(m.Groups[1].Value + ".", StringComparison.Ordinal))
                && !known.Contains(address))
                yield return address;
        }
    }

    /// <summary>Имена, которые короткое имя блока занять не может: диспетч-часть плюс верхнеуровневые
    /// имена библиотеки — она импортируется внутрь тела блока и перекрыла бы одноимённого соседа.</summary>
    private async Task<IReadOnlyCollection<string>> ReservedNamesAsync(CancellationToken ct)
    {
        var reserved = new HashSet<string>(StringComparer.Ordinal)
        {
            TypstPreambleBuilder.DispatchFnName,
            TypstPreambleBuilder.DispatchTableName,
            TypstPreambleBuilder.UnionSetName,
        };
        var snapshot = await userLib.GetAsync(ct);
        foreach (var text in new[] { snapshot.Entrypoint }.Concat(snapshot.Files.Select(f => f.Content)))
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(text ?? "", @"(?m)^#let\s+([\w\-]+)"))
                reserved.Add(m.Groups[1].Value);
        return reserved;
    }
}
