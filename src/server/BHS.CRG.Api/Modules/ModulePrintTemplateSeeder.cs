using BHS.CRG.Application.Generation;
using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Templates;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Printing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Api.Modules;

public static class ModulePrintTemplateSeeder
{
    public static async Task SeedModulePrintTemplatesAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        var modules = services.GetRequiredService<ModuleRegistry>();
        var forms = services.GetRequiredService<ModulePrintFormCatalog>();
        var defaults = forms.All.Where(e => e.Form.DefaultTemplate is not null && modules.IsEnabled(e.Module.Code));
        var db = services.GetRequiredService<AppDbContext>();
        var writer = services.GetRequiredService<ITemplateVersionWriter>();

        foreach (var entry in defaults)
        {
            var typeId = await db.DocumentTypes
                .Where(t => t.Code == entry.Form.TypeCode && t.Module == entry.Module.Code)
                .Select(t => (Guid?)t.Id).SingleOrDefaultAsync(ct);
            if (typeId is not { } id || await HasTemplateAsync(db, id, ct)) continue;

            var declared = entry.Form.DefaultTemplate!;
            var template = Template.Create(id, declared.Name, SystemTypstLib.EnsureImports(declared.Content));
            template.SetDefault(true);
            try
            {
                await writer.SaveRetiredThenInsertAsync([template], TemplateHandlers.NameTakenMessage, ct);
            }
            catch (ConflictException)
            {
                // Два старта объявляют одно название. Победитель уже положил шаблон, повтор не нужен.
                if (!await HasTemplateAsync(db, id, ct)) throw;
            }
        }
    }

    private static Task<bool> HasTemplateAsync(AppDbContext db, Guid typeId, CancellationToken ct) =>
        db.Templates.AnyAsync(t => t.DocumentTypeId == typeId, ct);
}
