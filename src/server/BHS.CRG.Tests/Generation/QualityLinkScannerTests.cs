using System.Text.Json;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Tests.Generation;

/// <summary>
/// Диагностика «материал без документа качества» (issue #585). Составной ключ строже прежнего
/// «совпало любое поле», поэтому несопоставленных материалов больше по построению — и пользователь
/// должен их видеть, раз решение (исправить материал или завести вторую связку) за ним.
/// </summary>
public class QualityLinkScannerTests
{
    private const string Target = "ДокументПодтверждающийКачество";

    private static DocumentType Composite(string name, string schema)
        => DocumentType.Create(name, name, DocumentTypeKind.Composite, null, JsonDocument.Parse(schema), false);

    private static DocumentType Document(string name, string schema)
        => DocumentType.Create(name, name, DocumentTypeKind.Document, null, JsonDocument.Parse(schema), false);

    private static readonly DocumentType Material = Composite("Материал", $$"""
        { "fields": [
            { "key": "Наименование", "tags": ["identity"] },
            { "key": "Артикул", "tags": ["identity"] },
            { "key": "{{Target}}", "type": "complex", "tags": ["material.qualityDocLink"] } ] }
        """);

    /// <summary>Объект строительства: «Наименование» с тэгом идентичности есть и у него, но нести
    /// документ качества он не может — материалом он не является (issue #569).</summary>
    private static readonly DocumentType Site = Composite("Объект строительства", """
        { "fields": [ { "key": "Наименование", "tags": ["identity"] } ] }
        """);

    private static readonly DocumentType Registry = Document("Реестр материалов", $$"""
        { "fields": [ { "key": "Материалы", "type": "array", "typeId": "{{Material.Id}}" } ] }
        """);

    /// <summary>Union-обёртка «массив ИЛИ ссылка на реестр» (#320) — из-за неё материалы АОСР лежат
    /// на два уровня вглубь.</summary>
    private static readonly DocumentType Wrapper = Composite("МатериалыАОСР", $$"""
        { "tags": ["type.union"], "fields": [
            { "key": "Материалы", "type": "array", "typeId": "{{Material.Id}}" },
            { "key": "Реестр", "type": "doc-ref", "typeId": "{{Registry.Id}}" } ] }
        """);

    private static readonly DocumentType Aosr = Document("АОСР", $$"""
        { "fields": [
            { "key": "Материалы", "type": "complex", "typeId": "{{Wrapper.Id}}" },
            { "key": "Объект", "type": "complex", "typeId": "{{Site.Id}}" } ] }
        """);

    private static readonly DocumentType[] AllTypes = [Material, Site, Wrapper, Registry, Aosr];

    /// <summary>Материалы прямым массивом — сценарий реестра.</summary>
    private static List<ResolutionDiagnostic> Scan(string materialsJson)
        => ScanContext(Registry, ("Материалы", materialsJson));

    private static List<ResolutionDiagnostic> ScanContext(DocumentType docType, params (string Key, string Json)[] data)
    {
        var ctx = new GenerationContext();
        foreach (var (key, json) in data)
            ctx.Set(key, JsonDocument.Parse(json).RootElement.Clone());
        var diagnostics = new List<ResolutionDiagnostic>();
        QualityLinkScanner.Scan(ctx, docType.Id, AllTypes, diagnostics);
        return diagnostics;
    }

    [Fact]
    public void MaterialWithoutQualityDoc_IsWarning()
    {
        var d = Assert.Single(Scan("""[ { "Наименование": "Трубка", "Артикул": "T-1" } ]"""));
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);   // выпуск не блокируем
        Assert.Equal(QualityLinkScanner.Code, d.Code);
        Assert.Equal($"Материалы[0].{Target}", d.Path);
        Assert.Contains("трубка | t-1", d.Message);             // в тексте — ключ, по которому искали
    }

    [Fact]
    public void MaterialWithQualityDoc_IsSilent()
        => Assert.Empty(Scan($$"""
            [ { "Наименование": "Трубка", "{{Target}}": { "НомерДокумента": "РОСС RU.0001" } } ]
            """));

    /// <summary>Пустой объект — не документ: резолвер такое поле считает незаполненным, и проверка
    /// обязана отвечать так же.</summary>
    [Fact]
    public void EmptyQualityDocObject_CountsAsMissing()
        => Assert.Single(Scan($$"""[ { "Наименование": "Трубка", "{{Target}}": {} } ]"""));

    /// <summary>Строка без единого поля идентичности материалом не считается — иначе предупреждение
    /// доставалось бы строкам, которые сопоставлять и не собирались (примечания, итоги).</summary>
    [Fact]
    public void RowWithoutIdentityValues_IsNotAMaterial()
        => Assert.Empty(Scan("""[ { "Примечание": "итого по разделу" }, { "Наименование": "  " } ]"""));

    /// <summary>Материал без артикула — обычный случай (пустой слот в ключе), и он тоже должен быть
    /// виден: именно такие теряются при переходе на составной ключ.</summary>
    [Fact]
    public void PartialIdentity_StillReported()
    {
        var d = Assert.Single(Scan("""[ { "Наименование": "Трубка" } ]"""));
        Assert.Contains("трубка | ", d.Message);
    }

    [Fact]
    public void TagsNotConfigured_ScannerStaysQuiet()
    {
        // Ни один тип не несёт тэгов материала — сопоставлять нечем, и молчание единственно верно.
        var plainRow = Composite("Позиция", """{ "fields": [ { "key": "Наименование" } ] }""");
        var plainDoc = Document("Ведомость", $$"""
            { "fields": [ { "key": "Материалы", "type": "array", "typeId": "{{plainRow.Id}}" } ] }
            """);
        var ctx = new GenerationContext();
        ctx.Set("Материалы", JsonDocument.Parse("""[ { "Наименование": "Трубка" } ]""").RootElement.Clone());
        var diagnostics = new List<ResolutionDiagnostic>();
        QualityLinkScanner.Scan(ctx, plainDoc.Id, [plainRow, plainDoc], diagnostics);
        Assert.Empty(diagnostics);
    }

    // ── правдоподобие привязанного документа (issue #586) ──────────────────────

    private const string Av125 = """
        { "Наименование": "EKF — автоматические выключатели",
          "Продукция": "Выключатели автоматические, торговой марки «EKF», модель: AV-125" }
        """;

    /// <summary>Тот самый случай: сертификат на автоматы держал 68 связок, включая термоусадку.</summary>
    [Fact]
    public void ForeignCertificate_IsReportedAsImplausible()
    {
        var d = Assert.Single(Scan($$"""
            [ { "Наименование": "Трубка термоусаживаемая ТУТ нг 20/10", "{{Target}}": {{Av125}} } ]
            """));
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Equal(QualityLinkScanner.ImplausibleCode, d.Code);
    }

    [Fact]
    public void CertificateFromTheSameScope_IsSilent()
        => Assert.Empty(Scan($$"""
            [ { "Наименование": "Выключатель автоматический AV-125 3P 63А EKF", "{{Target}}": {{Av125}} } ]
            """));

    /// <summary>Материал, опознанный одним артикулом, проверить нечем — и тревожить им нельзя.</summary>
    [Fact]
    public void BareArticle_NotReportedAsImplausible()
        => Assert.Empty(Scan($$"""
            [ { "Артикул": "mb15-07-01m-54", "{{Target}}": {{Av125}} } ]
            """));

    [Fact]
    public void IndexInPath_PointsAtTheRow()
    {
        var diagnostics = Scan("""
            [ { "Наименование": "Есть", "ДокументПодтверждающийКачество": { "Н": "1" } },
              { "Наименование": "Нет" } ]
            """);
        Assert.Equal($"Материалы[1].{Target}", Assert.Single(diagnostics).Path);
    }

    // ── материалы за составной обёрткой (issue #648) ───────────────────────────

    /// <summary>
    /// Inline-материалы АОСР лежат в «Материалы.Материалы» — внутри union-обёртки (#320). Обход
    /// только верхнего уровня их не видел: сертификат подставлялся бы, а сказать, что его нет, было
    /// бы некому.
    /// </summary>
    [Fact]
    public void MaterialInsideCompositeWrapper_IsReported()
    {
        var d = Assert.Single(ScanContext(Aosr,
            ("Материалы", """{ "Материалы": [ { "Наименование": "проверка", "Артикул": "2342" } ] }""")));
        Assert.Equal($"Материалы.Материалы[0].{Target}", d.Path);
    }

    /// <summary>
    /// Живой ложный вызов: обход по всему контексту (а не по схеме) выдал предупреждение «Материал
    /// «комплексная застройка «ДНС Сити»…» без документа качества» — претензию к объекту
    /// строительства. Тэг идентичности у него законный, а нести сертификат он не может.
    /// </summary>
    [Fact]
    public void ObjectOfNonMaterialType_IsNotAMaterial()
        => Assert.Empty(ScanContext(Aosr,
            ("Объект", """{ "Наименование": "комплексная застройка «ДНС Сити»" }""")));

    /// <summary>
    /// Вторая ветка того же union — ССЫЛКА на реестр. После резолва ссылок её реквизиты лежат в
    /// контексте целиком, но предупреждать по ним нельзя: закладка документа этих строк не
    /// показывает (клиент пропускает $ref), и человеку было бы некуда приложить сертификат. Свой
    /// список реестр проверяет сам, а иначе он повторялся бы в каждом ссылающемся документе.
    /// </summary>
    [Fact]
    public void MaterialsBehindDocRef_AreNotThisDocumentsProblem()
        => Assert.Empty(ScanContext(Aosr,
            ("Материалы", """
             { "Реестр": { "Материалы": [ { "Наименование": "Трубка", "Артикул": "T-1" } ] } }
             """)));

    /// <summary>Реквизиты подмешанного сертификата — не данные документа: искать материалы внутри
    /// них значит предъявлять претензии не по адресу.</summary>
    [Fact]
    public void InsideQualityDocRequisites_NotScanned()
        => Assert.Empty(Scan($$"""
            [ { "Наименование": "Выключатель автоматический AV-125 3P 63А EKF",
                "{{Target}}": { "Наименование": "EKF — автоматические выключатели",
                                "Продукция": "Выключатели автоматические, торговой марки «EKF», модель: AV-125" } } ]
            """));
}
