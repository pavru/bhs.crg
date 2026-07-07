using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Documents;

public class GeneratedFile : Entity
{
    public Guid DocumentInstanceId { get; private set; }
    public OutputFormat Format { get; private set; }
    public string BlobPath { get; private set; } = default!;

    /// <summary>Шаблон, которым сгенерирован файл (мульти-шаблонная генерация — один файл на шаблон).
    /// Null — сгенерировано до фичи мульти-шаблонов или без явного выбора (дефолт-шаблон типа).</summary>
    public Guid? TemplateId { get; private set; }

    private GeneratedFile() { }

    internal static GeneratedFile Create(Guid instanceId, OutputFormat format, string blobPath, Guid? templateId)
        => new() { DocumentInstanceId = instanceId, Format = format, BlobPath = blobPath, TemplateId = templateId };
}

public enum OutputFormat { Pdf }
