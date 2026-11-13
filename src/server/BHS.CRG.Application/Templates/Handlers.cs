using BHS.CRG.Application.Common;
using BHS.CRG.Application.Recent;
using BHS.CRG.Domain.Templates;
using MediatR;

namespace BHS.CRG.Application.Templates;

public class TemplateHandlers(
    IRepository<Template> repo,
    IRepository<TemplateAsset> templateAssetRepo,
    IDomainObjectRepository objRepo,
    IBlobStorage blobStorage,
    IDocumentTemplateInvalidator invalidator,
    IRecentRecords recent,
    ITemplateVersionWriter versionWriter) :
    IRequestHandler<CreateTemplateCommand, Template>,
    IRequestHandler<UpdateTemplateCommand, TemplateMutationResult>,
    IRequestHandler<SaveTemplateContentCommand, TemplateMutationResult>,
    IRequestHandler<DuplicateTemplateCommand, Template>,
    IRequestHandler<DeleteTemplateCommand>,
    IRequestHandler<GetTemplatesUsageQuery, IReadOnlyDictionary<Guid, TemplateUsage>>,
    IRequestHandler<GetActiveTemplateQuery, Template?>,
    IRequestHandler<ListTemplatesQuery, IReadOnlyList<Template>>,
    IRequestHandler<UpdateTemplateParametersCommand, Template>,
    IRequestHandler<SetTemplateDefaultCommand, TemplateMutationResult>
{
    public async Task<Template> Handle(CreateTemplateCommand cmd, CancellationToken ct)
    {
        // Стандартные импорты (systemlib + typeblocks) вставляем ТОЛЬКО при создании шаблона (issue #353):
        // дальше шаблон компилируется дословно, поэтому импорты обязаны жить в его содержимом.
        var content = Generation.SystemTypstLib.EnsureImports(cmd.Content);
        // Название обрезается по краям, но сравнивается точно — как группирует страница (D1).
        var name = (cmd.Name ?? "").Trim();
        await EnsureNameFreeAsync(cmd.DocumentTypeId, name, ct);
        var template = Template.Create(cmd.DocumentTypeId, name, content);
        await repo.AddAsync(template, ct);
        await repo.SaveChangesAsync(ct);
        return template;
    }

    public async Task<TemplateMutationResult> Handle(UpdateTemplateCommand cmd, CancellationToken ct)
    {
        var source = (await repo.FindAsync(t => t.Id == cmd.Id, ct)).SingleOrDefault()
            ?? throw new NotFoundException($"Template {cmd.Id} not found");
        // Все версии шаблона, а не одна названная (issue #1920): номер и единственная действующая
        // считаются по ним. Чтение без отслеживания — и исходную берём из того же списка, иначе
        // в контексте оказались бы два экземпляра одной строки.
        var versions = await repo.FindAsync(
            t => t.DocumentTypeId == source.DocumentTypeId && t.Name == source.Name, ct);
        // Версию могли удалить между двумя чтениями — это тоже «не найдена», а не 500.
        var existing = versions.SingleOrDefault(t => t.Id == cmd.Id)
            ?? throw new NotFoundException($"Template {cmd.Id} not found");
        // Меняются только версии с признаками — их и пишем: Update у неотслеживаемой строки
        // переписывает её целиком, вместе с Content каждой исторической версии.
        var touched = versions.Where(v => v.IsActive || v.IsDefault).ToList();
        // Гаснут действующие — именно они, а не исходная: от v2 при действующей v4 гаснет v4 (#1920).
        var retiring = versions.Where(v => v.IsActive).Select(v => v.Id).ToList();
        var newVersion = existing.CreateNewVersion(cmd.Content, versions, cmd.Comment);
        foreach (var v in touched) repo.Update(v);

        // Закреплённые за гаснущими версиями переходят на новую (вариант А #1923, D6): закрепление
        // значит «этот шаблон». Иначе документ оставался на погашенной, и генерация отказывала.
        // Выпущенные возвращаются в черновик — их PDF напечатан прежней версией. Блобы — после
        // коммита: откат не осиротит файлы.
        var replacedBy = retiring.ToDictionary(id => id, _ => newVersion.Id);
        var blobs = new List<string>();
        var repinnedReset = 0;
        foreach (var doc in (await objRepo.GetDocumentsOfTypeAsync(newVersion.DocumentTypeId, ct))
                     .Where(o => retiring.Any(o.PinsTemplate)))
        {
            doc.RepinTemplates(replacedBy);
            if (doc.Status != Domain.Documents.DocumentStatus.Draft)
            {
                blobs.AddRange(doc.ResetToDraft());
                repinnedReset++;
            }
            objRepo.Update(doc);
        }

        // Сначала погашение и пересадка, потом вставка — порядок, которого требует индекс (D4).
        // Проигравший гонку получает здесь 23505, адрес превращает его в 409 (D5).
        await versionWriter.SaveRetiredThenInsertAsync([newVersion], ct);
        foreach (var path in blobs) await blobStorage.DeleteAsync(path, ct);
        await DuplicateTemplateAssetsAsync(existing.Id, newVersion.Id, ct);
        // Документы без выбранного шаблона устарели (issue #362), если их шаблон сменился:
        // новая версия стала шаблоном по умолчанию — или его у типа нет, и они берут любой
        // действующий, а действующие только что сменились (issue #1920: от исторической версии
        // гасится действующая и не по умолчанию).
        var reset = newVersion.IsDefault || !await HasDefaultActiveAsync(newVersion.DocumentTypeId, ct)
            ? await invalidator.OnDefaultChangedAsync(newVersion.DocumentTypeId, ct)
            : 0;
        return new TemplateMutationResult(newVersion, reset + repinnedReset);
    }

    /// <summary>
    /// Название шаблона в типе не повторяется (#1924): шаблон — все версии с одним типом и одним
    /// названием, и создание под занятым названием добавляло бы действующую v1 в чужой шаблон.
    /// Гонку двух созданий ловит уникальный индекс (D3), здесь — внятный отказ без гонки.
    /// </summary>
    private async Task EnsureNameFreeAsync(Guid documentTypeId, string name, CancellationToken ct)
    {
        if (await IsNameTakenAsync(documentTypeId, name, ct))
            throw new ConflictException($"Шаблон с таким названием уже есть: «{name}».");
    }

    private async Task<bool> IsNameTakenAsync(Guid documentTypeId, string name, CancellationToken ct)
        => (await repo.FindAsync(t => t.DocumentTypeId == documentTypeId && t.Name == name, ct)).Count > 0;

    private async Task<bool> HasDefaultActiveAsync(Guid documentTypeId, CancellationToken ct)
        => (await repo.FindAsync(t => t.DocumentTypeId == documentTypeId && t.IsDefault && t.IsActive, ct)).Count > 0;

    // Простое сохранение (issue #360, Ctrl+S): правит содержимое активной версии на месте, без
    // новой версии и без дублирования ассетов (та же версия). Бросает, если версия не активна.
    public async Task<TemplateMutationResult> Handle(SaveTemplateContentCommand cmd, CancellationToken ct)
    {
        var template = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new NotFoundException($"Template {cmd.Id} not found");
        template.UpdateContent(cmd.Content);
        repo.Update(template);
        await repo.SaveChangesAsync(ct);
        // Содержимое версии изменилось на месте → устаревают документы, запиннутые на неё
        // (и no-pin, если версия дефолтная-активная) — сброс в Draft (issue #362).
        var reset = await invalidator.OnTemplateContentChangedAsync(template.Id, ct);
        return new TemplateMutationResult(template, reset);
    }

    public async Task<Template> Handle(DuplicateTemplateCommand cmd, CancellationToken ct)
    {
        var source = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new NotFoundException($"Template {cmd.Id} not found");
        string name;
        if (!string.IsNullOrWhiteSpace(cmd.NewName))
        {
            name = cmd.NewName.Trim();
            await EnsureNameFreeAsync(source.DocumentTypeId, name, ct);
        }
        else
        {
            // Без названия — первое свободное (D2): вторая «(копия)» влилась бы в первую.
            name = $"{source.Name} (копия)";
            for (var n = 2; await IsNameTakenAsync(source.DocumentTypeId, name, ct); n++)
                name = $"{source.Name} (копия {n})";
        }
        var copy = source.Duplicate(name);
        await repo.AddAsync(copy, ct);
        await repo.SaveChangesAsync(ct);
        await DuplicateTemplateAssetsAsync(source.Id, copy.Id, ct);
        return copy;
    }

    // Индивидуальные ассеты шаблона (issue #62) дублируются ПО ССЫЛКЕ на тот же blob (без
    // повторной загрузки байт) при создании новой версии/копии — до явной замены конкретного
    // ассета на новой версии (Handle(ReplaceTemplateAssetCommand) трогает только одну строку).
    private async Task DuplicateTemplateAssetsAsync(Guid fromTemplateId, Guid toTemplateId, CancellationToken ct)
    {
        var assets = await templateAssetRepo.FindAsync(
            a => a.Scope == TemplateAssetScope.Template && a.ScopeId == fromTemplateId, ct);
        foreach (var a in assets)
        {
            var copy = TemplateAsset.Create(
                TemplateAssetScope.Template, toTemplateId, a.Kind, a.Name, a.FileName, a.MimeType, a.BlobPath, a.FontFamilyName);
            await templateAssetRepo.AddAsync(copy, ct);
        }
        if (assets.Count > 0) await templateAssetRepo.SaveChangesAsync(ct);
    }

    public async Task Handle(DeleteTemplateCommand cmd, CancellationToken ct)
    {
        var t = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();

        // Документы, запиннутые на удаляемую версию, надо разрулить — иначе они осиротеют.
        var docs = await objRepo.GetDocumentsOfTypeAsync(t.DocumentTypeId, ct);
        var pinned = docs.Where(o => o.PinsTemplate(t.Id)).ToList();

        if (pinned.Count > 0 && !cmd.ReassignUsersToDefault)
            throw new ConflictException(
                $"Версия используется в {pinned.Count} докум. Удаление снимет привязку (документы вернутся на шаблон по умолчанию).");

        // Снимаем пин (→ резолв в дефолт) + сбрасываем PDF (контекст резолва сменится). Блобы — после коммита.
        var blobs = new List<string>();
        foreach (var o in pinned)
        {
            o.UnpinTemplate(t.Id);
            blobs.AddRange(o.ResetToDraft());
            objRepo.Update(o);
        }
        // Отметки «Недавних» удаляемой версии — к текущей из оставшихся, а последней — снимаются (shell-09,
        // Р-5): иначе очистка версий молча убирала бы живой шаблон с Главной.
        await recent.TemplateVersionLeavingAsync(t.Id, ct);
        repo.Remove(t);
        await repo.SaveChangesAsync(ct);
        foreach (var path in blobs) await blobStorage.DeleteAsync(path, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, TemplateUsage>> Handle(GetTemplatesUsageQuery q, CancellationToken ct)
    {
        var docs = await objRepo.GetDocumentsOfTypeAsync(q.DocumentTypeId, ct);
        // Проходим версии типа один раз, накапливая пины по templateId (+ примеры имён).
        var templates = await repo.FindAsync(t => t.DocumentTypeId == q.DocumentTypeId, ct);
        var acc = new Dictionary<Guid, (int Count, List<string> Names)>();
        foreach (var t in templates)
        {
            var users = docs.Where(o => o.PinsTemplate(t.Id)).ToList();
            if (users.Count == 0) continue;
            acc[t.Id] = (users.Count, users.Take(5).Select(o => o.DisplayName ?? "(без имени)").ToList());
        }
        return acc.ToDictionary(kv => kv.Key, kv => new TemplateUsage(kv.Value.Count, kv.Value.Names));
    }

    public async Task<Template?> Handle(GetActiveTemplateQuery q, CancellationToken ct)
    {
        var matches = await repo.FindAsync(t => t.DocumentTypeId == q.DocumentTypeId && t.IsActive, ct);
        return matches.FirstOrDefault();
    }

    public Task<IReadOnlyList<Template>> Handle(ListTemplatesQuery q, CancellationToken ct)
        => repo.FindAsync(t => t.DocumentTypeId == q.DocumentTypeId, ct);

    public async Task<Template> Handle(UpdateTemplateParametersCommand cmd, CancellationToken ct)
    {
        var template = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new NotFoundException($"Template {cmd.Id} not found");
        template.SetParameters(cmd.Parameters);
        repo.Update(template);
        await repo.SaveChangesAsync(ct);
        return template;
    }

    public async Task<TemplateMutationResult> Handle(SetTemplateDefaultCommand cmd, CancellationToken ct)
    {
        var all = await repo.GetAllAsync(ct);
        var target = all.FirstOrDefault(t => t.Id == cmd.Id)
            ?? throw new NotFoundException($"Template {cmd.Id} not found");

        foreach (var t in all.Where(t => t.DocumentTypeId == target.DocumentTypeId && t.IsDefault))
        {
            t.SetDefault(false);
            repo.Update(t);
        }

        target.SetDefault(true);
        repo.Update(target);
        await repo.SaveChangesAsync(ct);
        // Дефолт типа сменился → no-pin документы резолвятся в новый default-active → устарели (issue #362).
        var reset = await invalidator.OnDefaultChangedAsync(target.DocumentTypeId, ct);
        return new TemplateMutationResult(target, reset);
    }
}

public class TemplateAssetHandlers(IRepository<TemplateAsset> repo) :
    IRequestHandler<ListTemplateAssetsQuery, IReadOnlyList<TemplateAsset>>,
    IRequestHandler<CreateTemplateAssetCommand, TemplateAsset>,
    IRequestHandler<ReplaceTemplateAssetCommand, TemplateAsset>,
    IRequestHandler<DeleteTemplateAssetCommand>
{
    public Task<IReadOnlyList<TemplateAsset>> Handle(ListTemplateAssetsQuery q, CancellationToken ct)
        => repo.FindAsync(a => a.Scope == q.Scope && a.ScopeId == q.ScopeId, ct);

    public async Task<TemplateAsset> Handle(CreateTemplateAssetCommand cmd, CancellationToken ct)
    {
        // Адрес проверяет то же до загрузки файла; здесь — сторож для любого другого отправителя (D2).
        if (cmd.Kind == TemplateAssetKind.Image && !TemplateAsset.IsValidImageName(cmd.Name))
            throw new InvalidRequestException($"Имя ассета-картинки — имя файла, а не путь: {TemplateAsset.ImageNameRule}.");
        var asset = TemplateAsset.Create(
            cmd.Scope, cmd.ScopeId, cmd.Kind, cmd.Name, cmd.FileName, cmd.MimeType, cmd.BlobPath, cmd.FontFamilyName);
        await repo.AddAsync(asset, ct);
        await repo.SaveChangesAsync(ct);
        return asset;
    }

    public async Task<TemplateAsset> Handle(ReplaceTemplateAssetCommand cmd, CancellationToken ct)
    {
        var asset = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new NotFoundException($"TemplateAsset {cmd.Id} not found");
        asset.Replace(cmd.FileName, cmd.MimeType, cmd.BlobPath, cmd.FontFamilyName);
        repo.Update(asset);
        await repo.SaveChangesAsync(ct);
        return asset;
    }

    // Без проверки использования (issue #62): текстовый скан Typst-кода на предмет ссылки на этот
    // ассет давал бы ложные срабатывания в обе стороны (динамическое имя не найдёт, случайное
    // совпадение подстроки — ложно заблокирует) — хуже честного "Typst сам укажет на ошибку при
    // следующей генерации" (TypstGenerator пробрасывает stderr компилятора как текст исключения).
    public async Task Handle(DeleteTemplateAssetCommand cmd, CancellationToken ct)
    {
        var asset = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();
        repo.Remove(asset);
        await repo.SaveChangesAsync(ct);
    }
}
