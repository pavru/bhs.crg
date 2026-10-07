using BHS.CRG.Api.Modules;
using BHS.CRG.Tests.Support;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Recognition;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Infrastructure.Recognition;

namespace BHS.CRG.Tests.Recognition;

/// <summary>
/// Защита переезда на профили распознавания (issue #406): встроенные профили обязаны давать
/// ПОБУКВЕННО тот же промпт, что и прежние классы-константы. Это главный критерий приёмки —
/// переезд трогает самый используемый поток (распознавание альбома ГОСТ) и должен быть нейтральным.
/// (Повторное распознавание живого альбома таким критерием быть НЕ может: LLM недетерминирован,
/// это лишь проверка «нет катастрофы». Гарантию даёт только сравнение целой строки промпта.)
///
/// Поля прогоняются через реальный round-trip «сериализация в jsonb → чтение → RecognitionField»,
/// поэтому тест ловит и потерю описаний/вариантов при (де)сериализации, а не только сборку списка.
/// </summary>
public class RecognitionProfileMigrationTests
{
    private static ResolvedRecognitionProfile ThroughDb(string code)
    {
        var def = TestRecognition.Catalog.All.Single(d => d.Code == code);
        var profile = RecognitionProfile.CreateBuiltIn(
            def.Code, def.Name, def.Kind, def.Owner,
            RecognitionProfileJson.WriteFields(def.Fields),
            RecognitionProfileJson.WriteFieldsOrNull(def.RowColumns),
            RecognitionProfileJson.WriteShape(def.Shape),
            def.Hash);
        return RecognitionProfileJson.Resolve(profile);
    }

    [Fact]
    public void TitleBlock_PromptUnchanged()
    {
        Assert.Equal(
            RecognitionShared.BuildTitleBlockPrompt(TestRecognition.TitleBlock),
            RecognitionShared.BuildTitleBlockPrompt(ThroughDb(IdRecognitionProfiles.TitleBlockCode).ToRecognitionFields()));
    }

    [Fact]
    public void TitleBlockWithClassifiers_PromptUnchanged()
    {
        // Классификаторы в профиль не входят и подмешиваются кодом — блоки промпта про ТипСтраницы/
        // Форму включаются по факту их наличия, поэтому проверяем именно составленный набор.
        // Один профиль обслуживает ОБА живых пути (легаси-реестр по графам штампа и «3 источника» по
        // графам с классификаторами) — второй профиль для этого не нужен.
        Assert.Equal(
            RecognitionShared.BuildTitleBlockPrompt(GostTitleBlockFields.WithClassifiers(TestRecognition.TitleBlock)),
            RecognitionShared.BuildTitleBlockPrompt(GostTitleBlockFields.WithClassifiers(
                ThroughDb(IdRecognitionProfiles.TitleBlockCode).ToRecognitionFields())));
    }

    [Fact]
    public void CoverTitle_PromptUnchanged()
    {
        Assert.Equal(
            RecognitionShared.BuildCoverTitlePrompt(TestRecognition.CoverTitle),
            RecognitionShared.BuildCoverTitlePrompt(ThroughDb(IdRecognitionProfiles.CoverTitleCode).ToRecognitionFields()));
    }

    [Fact]
    public void Invoice_PromptUnchanged()
    {
        // Счёт — ОДИН вызов и ОДИН профиль: шапка в Fields, товары в RowColumns.
        Assert.Equal(
            RecognitionShared.BuildInvoicePrompt(TestRecognition.InvoiceCall),
            RecognitionShared.BuildInvoicePrompt(
                RecognitionKinds.ComposeCallFields(ThroughDb(CostsRecognitionProfiles.InvoiceCode))));
    }

    [Fact]
    public void SpecificationTable_PromptUnchanged()
    {
        var p = ThroughDb(IdRecognitionProfiles.SpecificationTableCode);
        Assert.Equal(
            RecognitionShared.BuildTablePrompt(
                GostTableFields.RecognitionFieldsFor(TestRecognition.Specification)),
            RecognitionShared.BuildTablePrompt(RecognitionKinds.ComposeCallFields(p), p.Shape));
    }

    [Fact]
    public void CableJournal_PromptUnchanged()
    {
        var p = ThroughDb(IdRecognitionProfiles.CableJournalCode);
        Assert.Equal(
            RecognitionShared.BuildCableJournalPrompt(
                GostTableFields.RecognitionFieldsFor(TestRecognition.CableJournal)),
            RecognitionShared.BuildCableJournalPrompt(RecognitionKinds.ComposeCallFields(p), p.Shape));
    }

    [Fact]
    public void TableColumnsFromDocumentType_ComposeIdenticallyToProfile()
    {
        // Механизм #29: колонки приходят из типа документа, но форма вызова та же — состав полей
        // обязан совпасть с профильным путём, иначе тип и профиль дали бы разные промпты.
        var p = ThroughDb(IdRecognitionProfiles.SpecificationTableCode);
        var asIfFromType = p.ToRowColumns();
        Assert.Equal(
            RecognitionKinds.ComposeCallFields(p).Select(f => f.Path),
            RecognitionKinds.ComposeCallFields(p.Kind, [], asIfFromType).Select(f => f.Path));
    }

    [Fact]
    public void Options_SurviveRoundTrip()
    {
        // «варианты: П, Р, И» печатаются в промпт — потеря Options тихо ухудшила бы распознавание.
        var vid = ThroughDb(IdRecognitionProfiles.TitleBlockCode).ToRecognitionFields()
            .Single(f => f.Path == "ВидДокументации");
        Assert.Equal(["П", "Р", "И"], vid.Options);
    }

    // ── Дескриптор вида: код, а не пользовательские данные ───────────────────────

    [Fact]
    public void SystemFields_ComeFromCodeDescriptor_NotFromProfileData()
    {
        // Признак «системное» намеренно не хранится в jsonb: иначе снимался бы через импорт/бэкап,
        // и защиту несущих полей можно было бы обойти подменённой копией.
        Assert.True(RecognitionKinds.IsSystemField(RecognitionProfileKind.TitleBlock, "Шифр"));
        Assert.True(RecognitionKinds.IsSystemField(RecognitionProfileKind.TitleBlock, "НаименованиеДокумента"));
        Assert.False(RecognitionKinds.IsSystemField(RecognitionProfileKind.TitleBlock, "Масштаб"));
        // У счёта несущих полей нет — форма не стандартизована, пользователь волен менять всё.
        Assert.Empty(RecognitionKinds.Describe(RecognitionProfileKind.Invoice).SystemFieldNames);
    }

    [Fact]
    public void EveryKind_HasDescriptor_AndTabularKindsCarryRowsKey()
    {
        foreach (var kind in Enum.GetValues<RecognitionProfileKind>())
        {
            var d = RecognitionKinds.Describe(kind); // бросит, если вид не описан
            Assert.Equal(kind, d.Kind);
        }
        Assert.True(RecognitionKinds.IsTabular(RecognitionProfileKind.Table));
        Assert.True(RecognitionKinds.IsTabular(RecognitionProfileKind.CableJournal));
        Assert.True(RecognitionKinds.IsTabular(RecognitionProfileKind.Invoice));   // товары
        Assert.False(RecognitionKinds.IsTabular(RecognitionProfileKind.TitleBlock));
    }

    [Fact]
    public void EveryBuiltInProfile_HasUniqueCode_AndNonEmptyParameters()
    {
        Assert.Equal(
            TestRecognition.Catalog.All.Select(d => d.Code).Distinct().Count(),
            TestRecognition.Catalog.All.Count);
        Assert.All(TestRecognition.Catalog.All, d => Assert.NotEmpty(d.Fields.Concat(d.RowColumns)));
    }

    [Fact]
    public void BuiltInHash_ChangesWithContent()
    {
        var def = TestRecognition.Catalog.All.Single(d => d.Code == IdRecognitionProfiles.CableJournalCode);
        var edited = def with { Fields = [.. def.Fields, new RecognitionProfileField("Новое")] };
        Assert.NotEqual(def.Hash, edited.Hash);
    }

    // ── Флаги формы (новая функциональность, не влияющая на дефолт) ──────────────

    [Fact]
    public void TableShape_DefaultAddsNothing()
    {
        var fields = GostTableFields.RecognitionFieldsFor(TestRecognition.Specification);
        Assert.Equal(
            RecognitionShared.BuildTablePrompt(fields),
            RecognitionShared.BuildTablePrompt(fields, new RecognitionTableShape()));
    }

    [Fact]
    public void TableShape_FlagsAddInstructions()
    {
        var fields = GostTableFields.RecognitionFieldsFor(TestRecognition.Specification);
        var prompt = RecognitionShared.BuildTablePrompt(fields,
            new RecognitionTableShape(TwoTierHeader: true, PairedSections: true, SkipTotals: false));

        Assert.Contains("ДВУХЭТАЖНАЯ", prompt);
        Assert.Contains("ПАРНЫЕ секции", prompt);
        Assert.Contains("ИТОГОВЫЕ строки включай", prompt);
        Assert.DoesNotContain("Заголовки/итоги/пустые строки НЕ включай.", prompt); // правило заменено, не продублировано
    }
}
