using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Documents;

/// <summary>
/// Собранный файл комплекта (склейка PDF всех документов комплекта по порядку). Отдельная сущность,
/// а не <see cref="GeneratedFile"/> (у того инвариант non-null instanceId и свой жизненный цикл) —
/// комплект другой концепт: одна строка на комплект, заменяется при каждой пересборке.
/// </summary>
public class DocumentSetOutput : Entity
{
    public Guid SetId { get; private set; }
    public string BlobPath { get; private set; } = default!;
    public OutputFormat Format { get; private set; }
    public DateTimeOffset GeneratedAt { get; private set; }

    private DocumentSetOutput() { }

    public static DocumentSetOutput Create(Guid setId, string blobPath, OutputFormat format)
        => new() { SetId = setId, BlobPath = blobPath, Format = format, GeneratedAt = DateTimeOffset.UtcNow };
}
