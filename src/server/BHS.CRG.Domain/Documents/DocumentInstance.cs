using System.Text.Json;
using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Documents;

public class DocumentInstance : Entity
{
    public Guid DocumentSetId { get; private set; }
    public Guid DocumentTypeId { get; private set; }

    /// <summary>Произвольное имя экземпляра. Null — отображается название типа документа.</summary>
    public string? Name { get; private set; }

    /// <summary>Реквизиты, введённые пользователем (скалярные поля).</summary>
    public JsonDocument Requisites { get; private set; } = JsonDocument.Parse("{}");

    /// <summary>Кэш данных из плагинов на момент последней генерации.</summary>
    public JsonDocument PluginData { get; private set; } = JsonDocument.Parse("{}");

    public DocumentStatus Status { get; private set; } = DocumentStatus.Draft;

    /// <summary>Явно выбранный шаблон (одиночная генерация / первый для параметров). Null — по умолчанию.</summary>
    public Guid? TemplateId { get; private set; }

    /// <summary>Набор выбранных шаблонов для мульти-генерации (JSON-массив Guid или null). Непусто —
    /// «Сгенерировать» создаёт по PDF на каждый; пусто — один PDF по <see cref="TemplateId"/>/дефолту.</summary>
    public string? TemplateIds { get; private set; }

    /// <summary>Переопределённые значения параметров шаблона (JSON-объект {имя:значение} или null).
    /// Дефолты берутся из <see cref="Templates.Template.Parameters"/>; здесь — только переопределения
    /// на уровне конкретного документа. Подмешиваются в контекст генерации под ключ «params».</summary>
    public string? TemplateParams { get; private set; }

    private readonly List<GeneratedFile> _generatedFiles = [];
    public IReadOnlyList<GeneratedFile> GeneratedFiles => _generatedFiles.AsReadOnly();

    private DocumentInstance() { }

    public static DocumentInstance Create(Guid documentSetId, Guid documentTypeId)
        => new() { DocumentSetId = documentSetId, DocumentTypeId = documentTypeId };

    public void UpdateRequisites(JsonDocument requisites)
    {
        Requisites = requisites;
        TouchUpdatedAt();
    }

    public void UpdatePluginData(JsonDocument pluginData)
    {
        PluginData = pluginData;
        TouchUpdatedAt();
    }

    public GeneratedFile AddGeneratedFile(OutputFormat format, string blobPath, Guid? templateId = null)
    {
        var file = GeneratedFile.Create(Id, format, blobPath, templateId);
        // Один файл на пару (формат, шаблон) — при мульти-шаблонной генерации файлы разных шаблонов
        // сосуществуют; повторная генерация тем же шаблоном заменяет его файл.
        _generatedFiles.RemoveAll(f => f.Format == format && f.TemplateId == templateId);
        _generatedFiles.Add(file);
        Status = DocumentStatus.Generated;
        TouchUpdatedAt();
        return file;
    }

    /// <summary>
    /// Сбрасывает документ в черновик и возвращает blob-пути удалённых файлов.
    /// Вызывается перед любым изменением реквизитов/шаблона, если статус не Draft.
    /// </summary>
    public IReadOnlyList<string> ResetToDraft()
    {
        if (Status == DocumentStatus.Draft) return [];
        var paths = _generatedFiles.Select(f => f.BlobPath).ToList();
        _generatedFiles.Clear();
        Status = DocumentStatus.Draft;
        TouchUpdatedAt();
        return paths;
    }

    public void Rename(string? name) { Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(); TouchUpdatedAt(); }
    public void SetTemplate(Guid? templateId) { TemplateId = templateId; TouchUpdatedAt(); }
    public void SetTemplateIds(string? templateIdsJson) { TemplateIds = templateIdsJson; TouchUpdatedAt(); }
    public void SetTemplateParams(string? paramsJson) { TemplateParams = paramsJson; TouchUpdatedAt(); }
    public void MarkGenerating() { Status = DocumentStatus.Generating; TouchUpdatedAt(); }
    public void MarkFailed() { Status = DocumentStatus.Failed; TouchUpdatedAt(); }
}

public enum DocumentStatus { Draft, Generating, Generated, Failed }
