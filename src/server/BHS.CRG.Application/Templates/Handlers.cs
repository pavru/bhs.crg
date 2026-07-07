using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Templates;
using MediatR;

namespace BHS.CRG.Application.Templates;

public class TemplateHandlers(IRepository<Template> repo) :
    IRequestHandler<CreateTemplateCommand, Template>,
    IRequestHandler<UpdateTemplateCommand, Template>,
    IRequestHandler<DuplicateTemplateCommand, Template>,
    IRequestHandler<DeleteTemplateCommand>,
    IRequestHandler<GetActiveTemplateQuery, Template?>,
    IRequestHandler<ListTemplatesQuery, IReadOnlyList<Template>>,
    IRequestHandler<UpdateTemplateSettingsCommand, Template>,
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
        return copy;
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

    public async Task<Template> Handle(UpdateTemplateSettingsCommand cmd, CancellationToken ct)
    {
        var template = await repo.GetByIdAsync(cmd.Id, ct)
            ?? throw new KeyNotFoundException($"Template {cmd.Id} not found");
        template.SetPageSettings(cmd.PageSize, cmd.PageOrientation, cmd.MarginTop, cmd.MarginRight, cmd.MarginBottom, cmd.MarginLeft);
        repo.Update(template);
        await repo.SaveChangesAsync(ct);
        return template;
    }

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
