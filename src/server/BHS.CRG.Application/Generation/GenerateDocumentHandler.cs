using System.Text;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Schema;
using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Notifications;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Domain.Templates;
using MediatR;

namespace BHS.CRG.Application.Generation;

public class GenerateDocumentHandler(
    IDomainObjectRepository instanceRepo,
    IRepository<GeneratedFile> fileRepo,
    IRepository<Template> templateRepo,
    IGenerationContextBuilder contextBuilder,
    IGenerationRequestBuilder requestBuilder,
    IDocumentGeneratorFactory generatorFactory,
    IBlobStorage blobStorage,
    IMetadataExtractor metadataExtractor,
    INotificationService notifications
) : IRequestHandler<GenerateDocumentCommand, IReadOnlyList<GeneratedFile>>
{
    public async Task<IReadOnlyList<GeneratedFile>> Handle(GenerateDocumentCommand cmd, CancellationToken ct)
    {
        var instance = await instanceRepo.GetByIdAsync(cmd.InstanceId, ct)
            ?? throw new NotFoundException($"DocumentInstance {cmd.InstanceId} not found");

        instance.MarkGenerating();
        instanceRepo.Update(instance);
        await instanceRepo.SaveChangesAsync(ct);

        try
        {
            var candidates = await templateRepo.FindAsync(t => t.DocumentTypeId == instance.CompositeTypeId, ct);

            // Шаблоны выпуска — общим правилом с предпросмотром и отладочным пакетом (TemplateChoice,
            // [ID-3]): выбранный набор в порядке выбора, иначе один эффективный.
            var templates = TemplateChoice.Require(instance, candidates);

            // Контекст и все проверки — тем же кодом, что у предпросмотра, проверки документа и
            // отладочного пакета (D1). Генерация отказывает на ошибке в ЛЮБОЙ корзине: незаполненное
            // обязательное поле выпуск останавливает, хотя контекст и собрался.
            var schemas = await contextBuilder.LoadSchemasAsync(ct);
            var built = await contextBuilder.BuildAsync(DocumentView.From(instance), cmd.Access, schemas, ct);
            var diagnostics = built.All;
            if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new ResolutionValidationException(diagnostics);

            // Штамп _type, блоки типов и библиотека — один раз на документ; на шаблон — params и ассеты (D2).
            var requests = await requestBuilder.PrepareAsync(
                built.Context, instance.CompositeTypeId, instance.TemplateParams, schemas, ct);

            var ext = cmd.Format == OutputFormat.Pdf ? "pdf" : "docx";
            var contentType = cmd.Format == OutputFormat.Pdf
                ? "application/pdf"
                : "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
            schemas.DocumentTypes.TryGetValue(instance.CompositeTypeId, out var docType);

            // По PDF на каждый выбранный шаблон. Контекст (реквизиты/наборы/каталог) общий — строится
            // один раз выше; на шаблон меняются только params, содержимое и ассеты.
            var generated = new List<GeneratedFile>();
            // Штамп метаданных откладывается до сохранения: он ляжет на данные, прочитанные под
            // блокировкой строки, а не на снимок, снятый до начала выпуска (issue #1232).
            Func<JsonDocument, JsonDocument>? stamp = null;
            foreach (var template in templates)
            {
                var generator = generatorFactory.Create(cmd.Format);
                var request = await requests.ForTemplateAsync(template, cmd.Format, ct);
                var bytes = await generator.GenerateAsync(request, ct);

                // Обратная запись метаданных — только с ПЕРВОГО файла (репрезентативно: число листов и т.п.).
                if (generated.Count == 0 && docType is not null)
                {
                    var taggedFields = SchemaTags.TaggedFields(docType, schemas.AllTypes);
                    if (taggedFields.Count > 0)
                    {
                        var meta = metadataExtractor.Extract(bytes, isPdf: cmd.Format == OutputFormat.Pdf, cmd.GeneratedBy);
                        stamp = data => SchemaTags.PatchMetadata(data, taggedFields, meta);
                    }
                }

                await using var ms = new MemoryStream(bytes);
                var blobPath = await blobStorage.UploadAsync($"{instance.Id}-{template.Id}.{ext}", ms, contentType, ct);
                var gf = instance.AddGeneratedFile(cmd.Format, blobPath, template.Id);
                await fileRepo.AddAsync(gf, ct);
                generated.Add(gf);
            }

            if (stamp is null) await instanceRepo.SaveChangesAsync(ct);
            else
            {
                // Выпуск длится секунды, и документ прочитан в его начале: реквизиты, сохранённые
                // формой за это время, штамп стёр бы целиком, хотя сам пишет два-три поля.
                await using var row = await instanceRepo.ReadForUpdateAsync([instance.Id], ct);
                instance.SetData(stamp(instance.Data));
                await row.SaveAsync(ct);
            }

            var first = generated[0];

            // Предупреждения раньше собирались и молча выбрасывались: уведомление говорило «готово», а
            // о полутора сотнях расхождений человек не узнавал — молчание читается как «всё в порядке».
            var warnings = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
            var what = generated.Count == 1
                ? $"«{instance.DisplayName}» — {cmd.Format}."
                : $"«{instance.DisplayName}» — сгенерировано файлов: {generated.Count}.";
            if (warnings > 0) what += $" Предупреждений: {warnings} — проверьте документ.";

            await notifications.PublishAsync(
                warnings > 0 ? NotificationSeverity.Warning : NotificationSeverity.Info,
                "Документ сгенерирован", what,
                "Генерация", userId: cmd.UserId,
                linkUrl: $"/api/generate/download/{instance.Id}/{first.TemplateId}/{ext}",
                linkLabel: generated.Count == 1 ? $"Скачать {ext.ToUpperInvariant()}" : "Открыть",
                ct: ct);

            return generated;
        }
        catch (Exception ex)
        {
            instance.MarkFailed();
            instanceRepo.Update(instance);
            await instanceRepo.SaveChangesAsync(ct);
            // Колокольчик — такой же выход наружу, как ответ на запрос: дословно уходит только НАШ
            // отказ (issue #691). Здесь стоял сырой ex.Message, то есть через уведомление уезжал и
            // вывод компилятора шаблона с путями папки прогона, и текст Npgsql со строкой
            // подключения при сбое сохранения. Теперь разобранный отказ компиляции (issue #1047)
            // доходит как есть, а чужая ошибка — общим текстом; целиком её пишет конвейер, который
            // получит это исключение ниже по throw.
            await notifications.PublishAsync(NotificationSeverity.Error, "Ошибка генерации",
                $"«{instance.DisplayName}»: " + Refusals.TextOr(ex,
                    "внутренняя ошибка — подробности в журнале сервера."),
                "Генерация", userId: cmd.UserId, ct: ct);
            throw;
        }
    }
}
