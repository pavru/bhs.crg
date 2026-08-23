using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Documents;

public class GeneratedFile : Entity
{
    /// <summary>Владелец — документная фасета объекта (issue #84: было DocumentInstanceId).</summary>
    public Guid ObjectId { get; private set; }
    public OutputFormat Format { get; private set; }
    public string BlobPath { get; private set; } = default!;

    /// <summary>Шаблон, которым сгенерирован файл (мульти-шаблонная генерация — один файл на шаблон).
    /// Null — сгенерировано до фичи мульти-шаблонов или без явного выбора (дефолт-шаблон типа).</summary>
    public Guid? TemplateId { get; private set; }

    private GeneratedFile() { }

    internal static GeneratedFile Create(Guid objectId, OutputFormat format, string blobPath, Guid? templateId)
        => new() { ObjectId = objectId, Format = format, BlobPath = blobPath, TemplateId = templateId };

    /// <summary>
    /// Восстановление из резервной копии (issue #833). Публичная, в отличие от <see cref="Create" />:
    /// создаёт файл только генерация (через фасету документа), а восстановление — снаружи агрегата.
    /// </summary>
    public static GeneratedFile Restore(Guid id, Guid objectId, OutputFormat format, string blobPath,
        Guid? templateId, DateTimeOffset createdAt, DateTimeOffset updatedAt)
        => new()
        {
            Id = id, ObjectId = objectId, Format = format, BlobPath = blobPath, TemplateId = templateId,
            CreatedAt = createdAt, UpdatedAt = updatedAt,
        };
}

public enum OutputFormat { Pdf }

/// <summary>Статус документа (документная фасета <c>DomainObject</c>).</summary>
public enum DocumentStatus { Draft, Generating, Generated, Failed }
