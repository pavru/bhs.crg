using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Recognition;
using BHS.CRG.Domain.Recognition;
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
        var def = BuiltInRecognitionProfiles.All.Single(d => d.Code == code);
        var profile = RecognitionProfile.CreateBuiltIn(
            def.Code, def.Name, def.Kind,
            RecognitionProfileJson.WriteFields(def.Fields),
            RecognitionProfileJson.WriteFieldsOrNull(def.RowColumns),
            RecognitionProfileJson.WriteShape(def.Shape),
            BuiltInRecognitionProfiles.HashOf(def));
        return RecognitionProfileJson.Resolve(profile);
    }

    [Fact]
    public void TitleBlock_PromptUnchanged()
    {
        Assert.Equal(
            RecognitionShared.BuildTitleBlockPrompt(GostTitleBlockFields.All),
            RecognitionShared.BuildTitleBlockPrompt(ThroughDb(BuiltInProfileCodes.TitleBlock).ToRecognitionFields()));
    }

    [Fact]
    public void TitleBlockWithClassifiers_PromptUnchanged()
    {
        // Классификаторы в профиль не входят и подмешиваются кодом — блоки промпта про ТипСтраницы/
        // Форму включаются по факту их наличия, поэтому проверяем именно составленный набор.
        // Один профиль обслуживает ОБА живых пути (легаси-реестр по All и «3 источника» по
        // AllWithClassifiers) — второй профиль для этого не нужен.
        Assert.Equal(
            RecognitionShared.BuildTitleBlockPrompt(GostTitleBlockFields.AllWithClassifiers),
            RecognitionShared.BuildTitleBlockPrompt(GostTitleBlockFields.WithClassifiers(
                ThroughDb(BuiltInProfileCodes.TitleBlock).ToRecognitionFields())));
    }

    [Fact]
    public void CoverTitle_PromptUnchanged()
    {
        Assert.Equal(
            RecognitionShared.BuildCoverTitlePrompt(GostCoverTitleFields.All),
            RecognitionShared.BuildCoverTitlePrompt(ThroughDb(BuiltInProfileCodes.CoverTitle).ToRecognitionFields()));
    }

    [Fact]
    public void Invoice_PromptUnchanged()
    {
        // Счёт — ОДИН вызов и ОДИН профиль: шапка в Fields, товары в RowColumns.
        Assert.Equal(
            RecognitionShared.BuildInvoicePrompt(InvoiceFields.All),
            RecognitionShared.BuildInvoicePrompt(
                RecognitionKinds.ComposeCallFields(ThroughDb(BuiltInProfileCodes.Invoice))));
    }

    [Fact]
    public void SpecificationTable_PromptUnchanged()
    {
        var p = ThroughDb(BuiltInProfileCodes.SpecificationTable);
        Assert.Equal(
            RecognitionShared.BuildTablePrompt(
                GostTableFields.RecognitionFieldsFor(GostTableFields.SpecificationColumns)),
            RecognitionShared.BuildTablePrompt(RecognitionKinds.ComposeCallFields(p), p.Shape));
    }

    [Fact]
    public void CableJournal_PromptUnchanged()
    {
        var p = ThroughDb(BuiltInProfileCodes.CableJournal);
        Assert.Equal(
            RecognitionShared.BuildCableJournalPrompt(
                GostTableFields.RecognitionFieldsFor(GostTableFields.CableJournalColumns)),
            RecognitionShared.BuildCableJournalPrompt(RecognitionKinds.ComposeCallFields(p), p.Shape));
    }

    [Fact]
    public void TableColumnsFromDocumentType_ComposeIdenticallyToProfile()
    {
        // Механизм #29: колонки приходят из типа документа, но форма вызова та же — состав полей
        // обязан совпасть с профильным путём, иначе тип и профиль дали бы разные промпты.
        var p = ThroughDb(BuiltInProfileCodes.SpecificationTable);
        var asIfFromType = p.ToRowColumns();
        Assert.Equal(
            RecognitionKinds.ComposeCallFields(p).Select(f => f.Path),
            RecognitionKinds.ComposeCallFields(p.Kind, [], asIfFromType).Select(f => f.Path));
    }

    [Fact]
    public void Options_SurviveRoundTrip()
    {
        // «варианты: П, Р, И» печатаются в промпт — потеря Options тихо ухудшила бы распознавание.
        var vid = ThroughDb(BuiltInProfileCodes.TitleBlock).ToRecognitionFields()
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
            BuiltInRecognitionProfiles.All.Select(d => d.Code).Distinct().Count(),
            BuiltInRecognitionProfiles.All.Count);
        Assert.All(BuiltInRecognitionProfiles.All, d => Assert.NotEmpty(d.Fields.Concat(d.RowColumns)));
    }

    [Fact]
    public void BuiltInHash_ChangesWithContent()
    {
        var def = BuiltInRecognitionProfiles.All.Single(d => d.Code == BuiltInProfileCodes.CableJournal);
        var edited = def with { Fields = [.. def.Fields, new RecognitionProfileField("Новое")] };
        Assert.NotEqual(BuiltInRecognitionProfiles.HashOf(def), BuiltInRecognitionProfiles.HashOf(edited));
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
