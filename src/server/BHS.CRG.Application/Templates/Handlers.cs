using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Templates;
using MediatR;

namespace BHS.CRG.Application.Templates;

public class TemplateHandlers(IRepository<Template> repo, IRepository<TemplateAsset> templateAssetRepo) :
    IRequestHandler<CreateTemplateCommand, Template>,
    IRequestHandler<UpdateTemplateCommand, Template>,
    IRequestHandler<DuplicateTemplateCommand, Template>,
    IRequestHandler<DeleteTemplateCommand>,
    IRequestHandler<GetActiveTemplateQuery, Template?>,
    IRequestHandler<ListTemplatesQuery, IReadOnlyList<Template>>,
    IRequestHandler<UpdateTemplateParametersCommand, Template>,
    IRequestHandler<SetTemplateDefaultCommand, Template>
{
    public async Task<Template> Handle(CreateTemplateCommand cmd, CancellationToken ct)
    {
        var template = Template.Create(cmd.DocumentTypeId, cmd.Name, cmd.Content);
        await repo.AddAsync(template, ct);
        await repo.SaveChangesAsync(ct);
        return template;
    }

    public async Task<Template> Handle(UpdateTemplateCommand cmd, CancellationToken ct)
    {
        var existing = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new KeyNotFoundException($"Template {cmd.Id} not found");
        var newVersion = existing.CreateNewVersion(cmd.Content);
        repo.Update(existing);
        await repo.AddAsync(newVersion, ct);
        await repo.SaveChangesAsync(ct);
        await DuplicateTemplateAssetsAsync(existing.Id, newVersion.Id, ct);
        return newVersion;
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
        repo.Remove(t);
        await repo.SaveChangesAsync(ct);
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

    public async Task<Template> Handle(SetTemplateDefaultCommand cmd, CancellationToken ct)
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
        return target;
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
