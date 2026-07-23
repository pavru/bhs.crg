using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Templates;
using MediatR;

namespace BHS.CRG.Application.Templates;

public class TemplateHandlers(
    IRepository<Template> repo,
    IRepository<TemplateAsset> templateAssetRepo,
    IDomainObjectRepository objRepo,
    IBlobStorage blobStorage,
    IDocumentTemplateInvalidator invalidator) :
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
        var template = Template.Create(cmd.DocumentTypeId, cmd.Name, content);
        await repo.AddAsync(template, ct);
        await repo.SaveChangesAsync(ct);
        return template;
    }

    public async Task<TemplateMutationResult> Handle(UpdateTemplateCommand cmd, CancellationToken ct)
    {
        var existing = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new KeyNotFoundException($"Template {cmd.Id} not found");
        var newVersion = existing.CreateNewVersion(cmd.Content, cmd.Comment);
        repo.Update(existing);
        await repo.AddAsync(newVersion, ct);
        await repo.SaveChangesAsync(ct);
        await DuplicateTemplateAssetsAsync(existing.Id, newVersion.Id, ct);
        // Если новая версия — дефолтная, эффективный default-active сместился на неё → no-pin
        // документы этого типа устарели (issue #362). Запиннутые на старую версию не трогаем.
        var reset = newVersion.IsDefault
            ? await invalidator.OnDefaultChangedAsync(newVersion.DocumentTypeId, ct)
            : 0;
        return new TemplateMutationResult(newVersion, reset);
    }

    // Простое сохранение (issue #360, Ctrl+S): правит содержимое активной версии на месте, без
    // новой версии и без дублирования ассетов (та же версия). Бросает, если версия не активна.
    public async Task<TemplateMutationResult> Handle(SaveTemplateContentCommand cmd, CancellationToken ct)
    {
        var template = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new KeyNotFoundException($"Template {cmd.Id} not found");
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
            ?? throw new KeyNotFoundException($"Template {cmd.Id} not found");
        var name = string.IsNullOrWhiteSpace(cmd.NewName) ? $"{source.Name} (копия)" : cmd.NewName.Trim();
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
        var t = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new KeyNotFoundException();

        // Документы, запиннутые на удаляемую версию, надо разрулить — иначе они осиротеют.
        var docs = await objRepo.GetDocumentsOfTypeAsync(t.DocumentTypeId, ct);
        var pinned = docs.Where(o => o.PinsTemplate(t.Id)).ToList();

        if (pinned.Count > 0 && !cmd.ReassignUsersToDefault)
            throw new InvalidOperationException(
                $"Версия используется в {pinned.Count} докум. Удаление снимет привязку (документы вернутся на шаблон по умолчанию).");

        // Снимаем пин (→ резолв в дефолт) + сбрасываем PDF (контекст резолва сменится). Блобы — после коммита.
        var blobs = new List<string>();
        foreach (var o in pinned)
        {
            o.UnpinTemplate(t.Id);
            blobs.AddRange(o.ResetToDraft());
            objRepo.Update(o);
        }
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
            ?? throw new KeyNotFoundException($"Template {cmd.Id} not found");
        template.SetParameters(cmd.Parameters);
        repo.Update(template);
        await repo.SaveChangesAsync(ct);
        return template;
    }

    public async Task<TemplateMutationResult> Handle(SetTemplateDefaultCommand cmd, CancellationToken ct)
    {
        var all = await repo.GetAllAsync(ct);
        var target = all.FirstOrDefault(t => t.Id == cmd.Id)
            ?? throw new KeyNotFoundException($"Template {cmd.Id} not found");

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
        var asset = TemplateAsset.Create(
            cmd.Scope, cmd.ScopeId, cmd.Kind, cmd.Name, cmd.FileName, cmd.MimeType, cmd.BlobPath, cmd.FontFamilyName);
        await repo.AddAsync(asset, ct);
        await repo.SaveChangesAsync(ct);
        return asset;
    }

    public async Task<TemplateAsset> Handle(ReplaceTemplateAssetCommand cmd, CancellationToken ct)
    {
        var asset = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new KeyNotFoundException($"TemplateAsset {cmd.Id} not found");
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
        var asset = await repo.GetByIdAsync(cmd.Id, ct) ?? throw new KeyNotFoundException();
        repo.Remove(asset);
        await repo.SaveChangesAsync(ct);
    }
}
