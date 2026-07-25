using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Recognition;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Infrastructure.Recognition;

namespace BHS.CRG.Tests.Recognition;

/// <summary>
/// Защита переезда на профили распознавания (issue #406): встроенные профили обязаны давать
/// ПОБУКВЕННО тот же промпт, что и прежние классы-константы. Это главный критерий приёмки —
/// переезд трогает самый используемый поток (распознавание альбома ГОСТ) и должен быть нейтральным.
///
/// Поля прогоняются через реальный round-trip «сериализация в jsonb → чтение → RecognitionField»,
/// поэтому тест ловит и потерю описаний/вариантов при (де)сериализации, а не только сборку списка.
/// </summary>
public class RecognitionProfileMigrationTests
{
    private static IReadOnlyList<RecognitionField> ThroughDb(string code)
    {
        var def = BuiltInRecognitionProfiles.All.Single(d => d.Code == code);
        var profile = RecognitionProfile.CreateBuiltIn(
            def.Code, def.Name, def.Kind,
            RecognitionProfileJson.WriteFields(def.Fields),
            RecognitionProfileJson.WriteShape(def.Shape));
        return RecognitionProfileJson.Resolve(profile).ToRecognitionFields();
    }

    private static RecognitionTableShape? ShapeOf(string code)
    {
        var def = BuiltInRecognitionProfiles.All.Single(d => d.Code == code);
        var profile = RecognitionProfile.CreateBuiltIn(
            def.Code, def.Name, def.Kind,
            RecognitionProfileJson.WriteFields(def.Fields),
            RecognitionProfileJson.WriteShape(def.Shape));
        return RecognitionProfileJson.Resolve(profile).Shape;
    }

    [Fact]
    public void TitleBlock_PromptUnchanged()
    {
        Assert.Equal(
            RecognitionShared.BuildTitleBlockPrompt(GostTitleBlockFields.All),
            RecognitionShared.BuildTitleBlockPrompt(ThroughDb(BuiltInProfileCodes.TitleBlock)));
    }

    [Fact]
    public void TitleBlockWithClassifiers_PromptUnchanged()
    {
        // Классификаторы в профиль не входят и подмешиваются кодом — блоки промпта про ТипСтраницы/
        // Форму включаются по факту их наличия, поэтому проверяем именно составленный набор.
        Assert.Equal(
            RecognitionShared.BuildTitleBlockPrompt(GostTitleBlockFields.AllWithClassifiers),
            RecognitionShared.BuildTitleBlockPrompt(
                GostTitleBlockFields.WithClassifiers(ThroughDb(BuiltInProfileCodes.TitleBlock))));
    }

    [Fact]
    public void CoverTitle_PromptUnchanged()
    {
        Assert.Equal(
            RecognitionShared.BuildCoverTitlePrompt(GostCoverTitleFields.All),
            RecognitionShared.BuildCoverTitlePrompt(ThroughDb(BuiltInProfileCodes.CoverTitle)));
    }

    [Fact]
    public void Invoice_PromptUnchanged()
    {
        // Счёт распознаётся одним вызовом — поля собираются из ДВУХ профилей.
        var composed = InvoiceFields.Compose(
            ThroughDb(BuiltInProfileCodes.InvoiceHeader),
            ThroughDb(BuiltInProfileCodes.InvoiceLineItems));
        Assert.Equal(
            RecognitionShared.BuildInvoicePrompt(InvoiceFields.All),
            RecognitionShared.BuildInvoicePrompt(composed));
    }

    [Fact]
    public void SpecificationTable_PromptUnchanged()
    {
        var code = BuiltInProfileCodes.SpecificationTable;
        Assert.Equal(
            RecognitionShared.BuildTablePrompt(
                GostTableFields.RecognitionFieldsFor(GostTableFields.SpecificationColumns)),
            RecognitionShared.BuildTablePrompt(
                GostTableFields.RecognitionFieldsFor(ThroughDb(code)), ShapeOf(code)));
    }

    [Fact]
    public void CableJournal_PromptUnchanged()
    {
        var code = BuiltInProfileCodes.CableJournal;
        Assert.Equal(
            RecognitionShared.BuildCableJournalPrompt(
                GostTableFields.RecognitionFieldsFor(GostTableFields.CableJournalColumns)),
            RecognitionShared.BuildCableJournalPrompt(
                GostTableFields.RecognitionFieldsFor(ThroughDb(code)), ShapeOf(code)));
    }

    [Fact]
    public void Options_SurviveRoundTrip()
    {
        // «варианты: П, Р, И» печатаются в промпт — потеря Options тихо ухудшила бы распознавание.
        var vid = ThroughDb(BuiltInProfileCodes.TitleBlock).Single(f => f.Path == "ВидДокументации");
        Assert.Equal(["П", "Р", "И"], vid.Options);
    }

    [Fact]
    public void SystemFields_MarkedOnLoadBearingStampFields()
    {
        // На этих двух ключах держится разбиение альбома: НаименованиеДокумента — авто-тэггер таблиц
        // и проекция «Документы», Шифр — определение границы документа.
        var def = BuiltInRecognitionProfiles.All.Single(d => d.Code == BuiltInProfileCodes.TitleBlock);
        var system = def.Fields.Where(f => f.IsSystem).Select(f => f.Name).ToHashSet();
        Assert.Equal(new HashSet<string> { "Шифр", "НаименованиеДокумента" }, system);
    }

    [Fact]
    public void EveryBuiltInKind_HasExactlyOneProfile()
    {
        // Вид выбирает стратегию (промпт); дубль кода или пропущенный вид сломали бы резолв.
        Assert.Equal(
            BuiltInRecognitionProfiles.All.Select(d => d.Code).Distinct().Count(),
            BuiltInRecognitionProfiles.All.Count);
        Assert.All(BuiltInRecognitionProfiles.All, d => Assert.NotEmpty(d.Fields));
    }

    // ── Флаги формы (новая функциональность, не влияющая на дефолт) ──────────────

    [Fact]
    public void TableShape_DefaultAddsNothing()
    {
        var fields = GostTableFields.RecognitionFieldsFor(GostTableFields.SpecificationColumns);
        Assert.Equal(
            RecognitionShared.BuildTablePrompt(fields),
            RecognitionShared.BuildTablePrompt(fields, new RecognitionTableShape()));
    }

    [Fact]
    public void TableShape_FlagsAddInstructions()
    {
        var fields = GostTableFields.RecognitionFieldsFor(GostTableFields.SpecificationColumns);
        var prompt = RecognitionShared.BuildTablePrompt(fields,
            new RecognitionTableShape(TwoTierHeader: true, PairedSections: true, SkipTotals: false));

        Assert.Contains("ДВУХЭТАЖНАЯ", prompt);
        Assert.Contains("ПАРНЫЕ секции", prompt);
        Assert.Contains("ИТОГОВЫЕ строки включай", prompt);
        Assert.DoesNotContain("Заголовки/итоги/пустые строки НЕ включай.", prompt); // правило заменено, не продублировано
    }
}
