using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Templates;
using MediatR;

namespace BHS.CRG.Application.Generation;

public record PrintRecordCommand(
    string TypeCode, string Owner, Guid? TemplateId, JsonElement Data, bool DebugBundle = false) : IRequest<byte[]>;

public sealed class PrintRecordHandler(
    IRepository<DocumentType> types,
    IRepository<Template> templates,
    ITemplateNames templateNames,
    IGenerationContextBuilder contextBuilder,
    IGenerationRequestBuilder requestBuilder,
    IDocumentGeneratorFactory generatorFactory) : IRequestHandler<PrintRecordCommand, byte[]>
{
    public async Task<byte[]> Handle(PrintRecordCommand command, CancellationToken ct)
    {
        var type = (await types.FindAsync(t => t.Code == command.TypeCode && t.Module == command.Owner, ct))
            .SingleOrDefault() ?? throw new NotFoundException($"Тип печатной формы «{command.TypeCode}» не найден.");

        var template = command.TemplateId is { } id
            ? await templates.GetByIdAsync(id, ct)
            : await TemplateChoice.FallbackAsync(type.Id, templates, templateNames, ct);
        if (command.TemplateId is not null && (template is null || template.DocumentTypeId != type.Id))
            throw new NotFoundException("Шаблон этой печатной формы не найден.");
        if (template is null)
            throw new ConflictException("У типа нет шаблона печати.");
        if (!template.IsActive)
            throw new ConflictException("Выбранный шаблон печати не активен.");

        var context = new GenerationContext();
        foreach (var field in command.Data.EnumerateObject())
            context.Set(field.Name, field.Value);

        var schemas = await contextBuilder.LoadSchemasAsync(ct);
        var requests = await requestBuilder.PrepareAsync(context, type.Id, null, schemas, ct);
        var request = await requests.ForTemplateAsync(template, OutputFormat.Pdf, ct);
        var generator = generatorFactory.Create(OutputFormat.Pdf);
        return command.DebugBundle
            ? await generator.DebugBundleAsync(request, BundleNotes.None, ct)
            : await generator.GenerateAsync(request, ct);
    }
}
