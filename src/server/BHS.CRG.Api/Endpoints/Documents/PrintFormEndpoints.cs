using System.Security.Claims;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Api.Endpoints.Documents;

public static class PrintFormEndpoints
{
    public static void MapPrintFormEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/document-sets").RequireAuthorization();

        // POST /api/document-sets/{setId}/documents/{instanceId}/print-form?fieldKey=xxx
        g.MapPost("/{setId:guid}/documents/{instanceId:guid}/print-form",
            async (
                Guid setId,
                Guid instanceId,
                string fieldKey,
                IFormFile file,
                IRepository<DocumentInstance> instanceRepo,
                IRepository<DocumentType> docTypeRepo,
                IBlobStorage blob,
                IMetadataExtractor metadataExtractor,
                ClaimsPrincipal user,
                CancellationToken ct) =>
            {
                var instance = await instanceRepo.GetByIdAsync(instanceId, ct);
                if (instance is null || instance.DocumentSetId != setId)
                    return Results.NotFound();

                const long maxSize = 50 * 1024 * 1024;
                if (file.Length > maxSize)
                    return Results.BadRequest(new { error = "Файл превышает 50 МБ" });

                // Читаем байты один раз — нужны и для upload, и для метаданных
                byte[] bytes;
                await using (var stream = file.OpenReadStream())
                {
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms, ct);
                    bytes = ms.ToArray();
                }

                // Загружаем в хранилище
                await using var uploadStream = new MemoryStream(bytes);
                var blobPath = await blob.UploadAsync(file.FileName, uploadStream, file.ContentType, ct);

                // Строим FileAttachment (формат, совместимый с $type: 'file' на фронтенде)
                var attachment = new Dictionary<string, object>
                {
                    ["$type"]    = "file",
                    ["blobPath"] = blobPath,
                    ["fileName"] = file.FileName,
                    ["mimeType"] = file.ContentType,
                    ["size"]     = file.Length,
                };

                // Извлекаем метаданные из файла
                var isPdf = file.ContentType == "application/pdf";
                var generatedBy = user.FindFirst("displayName")?.Value;
                var meta = metadataExtractor.Extract(bytes, isPdf, generatedBy);

                // Находим все тегированные поля типа документа
                var allDocTypes = await docTypeRepo.GetAllAsync(ct);
                var docType = allDocTypes.FirstOrDefault(dt => dt.Id == instance.DocumentTypeId);

                var updatedFields = new Dictionary<string, object?>();

                // Сначала записываем сам файл в printForm-поле
                updatedFields[fieldKey] = attachment;

                // Затем — все метаданные (кроме printForm-тега самого поля)
                if (docType is not null)
                {
                    var tagged = SchemaTags.TaggedFields(docType, allDocTypes);
                    foreach (var (key, tag) in tagged)
                    {
                        if (tag == FunctionalTag.DocPrintForm) continue; // само поле уже выше
                        if (meta.TryGetValue(tag, out var val))
                            updatedFields[key] = val;
                    }
                }

                // Патчим реквизиты и сохраняем
                var current = instance.Requisites;
                var dict = new Dictionary<string, JsonElement>();
                foreach (var p in current.RootElement.EnumerateObject())
                    dict[p.Name] = p.Value.Clone();
                foreach (var (k, v) in updatedFields)
                    dict[k] = JsonSerializer.SerializeToElement(v);

                instance.UpdateRequisites(JsonDocument.Parse(JsonSerializer.Serialize(dict)));
                instanceRepo.Update(instance);
                await instanceRepo.SaveChangesAsync(ct);

                return Results.Ok(new { updatedFields });
            })
            .DisableAntiforgery();
    }
}
