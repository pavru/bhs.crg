using BHS.CRG.Infrastructure.Recognition;

namespace BHS.CRG.Tests.Recognition;

public class GostPageGrouperTests
{
    private static Dictionary<string, string?> Page(string? pageType, string? shifr = null, string? documentName = null, string? form = null) =>
        new()
        {
            [GostTitleBlockFields.PageTypePath] = pageType,
            [GostTitleBlockFields.StampFormPath] = form,
            ["Шифр"] = shifr,
            ["НаименованиеДокумента"] = documentName,
        };

    [Fact]
    public void RoutesCoverAndTitlePagePages()
    {
        var result = GostPageGrouper.Group([Page("Обложка"), Page("ТитульныйЛист"), Page("Документ", "01-ЭМ", form: "Форма3")]);

        Assert.Single(result.Cover);
        Assert.Single(result.TitlePage);
        Assert.Single(result.Documents);
    }

    [Fact]
    public void ClassifierKeys_DoNotLeakIntoOutputRows()
    {
        var result = GostPageGrouper.Group([Page("Обложка", form: "Форма3"), Page("Документ", "01-ЭМ", form: "Форма3")]);

        Assert.DoesNotContain(GostTitleBlockFields.PageTypePath, result.Cover[0].Fields.Keys);
        Assert.DoesNotContain(GostTitleBlockFields.StampFormPath, result.Cover[0].Fields.Keys);
        Assert.DoesNotContain(GostTitleBlockFields.PageTypePath, result.Documents[0].Fields.Keys);
        Assert.DoesNotContain(GostTitleBlockFields.StampFormPath, result.Documents[0].Fields.Keys);
    }

    [Fact]
    public void FirstSheetPlusForm6Continuations_GroupTogether_WithPageCount()
    {
        var pages = new[]
        {
            Page("Документ", "01-ЭМ", "План этажа", form: "Форма3"),
            Page("Документ", "01-ЭМ", documentName: null, form: "Форма6"),
            Page("Документ", "02-ЭМ", "Разрез", form: "Форма3"),
        };

        var result = GostPageGrouper.Group(pages);

        Assert.Equal(2, result.Documents.Count);
        var planEtazha = result.Documents[0];
        Assert.Equal("01-ЭМ", planEtazha.Code);
        Assert.Equal([0, 1], planEtazha.PageIndices);
        Assert.Equal("2", planEtazha.Fields["КоличествоЛистов"]);
        Assert.Equal("План этажа", planEtazha.Fields["НаименованиеДокумента"]);

        var razrez = result.Documents[1];
        Assert.Equal([2], razrez.PageIndices);
        Assert.Equal("1", razrez.Fields["КоличествоЛистов"]);
    }

    [Fact]
    public void EmptyShifr_FallsIntoDefaultBucket()
    {
        var result = GostPageGrouper.Group([Page("Документ", shifr: null, form: "Форма3"), Page("Документ", shifr: "", form: "Форма6")]);

        var group = Assert.Single(result.Documents);
        Assert.Equal("(без шифра)", group.Code);
        Assert.Equal("2", group.Fields["КоличествоЛистов"]);
    }

    [Fact]
    public void UnknownOrMissingPageType_TreatedAsDocument()
    {
        var result = GostPageGrouper.Group([Page(pageType: null, shifr: "01-ЭМ", form: "Форма3"), Page(pageType: "что-то странное", shifr: "01-ЭМ", form: "Форма6")]);

        Assert.Empty(result.Cover);
        Assert.Empty(result.TitlePage);
        var group = Assert.Single(result.Documents);
        Assert.Equal("2", group.Fields["КоличествоЛистов"]);
    }

    [Fact]
    public void FirstNonEmptyValuePerField_IsKeptAcrossGroupPages()
    {
        // Первый лист + два продолжения (форма 6) — заведомо одна группа; проверяем агрегацию
        // прочих полей: значение из более ранней страницы выигрывает у более поздних непустых.
        var pages = new[]
        {
            Page("Документ", "01-ЭМ", "План этажа", form: "Форма3"),
            Page("Документ", "01-ЭМ", form: "Форма6"),
            Page("Документ", "01-ЭМ", form: "Форма6"),
        };
        pages[0]["Организация"] = null;
        pages[1]["Организация"] = "Институт";
        pages[2]["Организация"] = "ДРУГОЕ";

        var result = GostPageGrouper.Group(pages);
        var group = Assert.Single(result.Documents);
        Assert.Equal("Институт", group.Fields["Организация"]);
        Assert.Equal([0, 1, 2], group.PageIndices);
    }

    /// <summary>
    /// ГОСТ Р 21.101-2020: форма 5 (первый/заглавный лист текстового документа) + форма 6
    /// (последующие листы). Должны собраться в один документ.
    /// </summary>
    [Fact]
    public void Form5FirstSheetAndForm6Continuation_GroupTogether()
    {
        var pages = new[]
        {
            Page("Документ", shifr: "05-АР", documentName: "Пояснительная записка", form: "Форма5"),
            Page("Документ", shifr: "05-АР", documentName: null, form: "Форма6"),
            Page("Документ", shifr: "05-АР", documentName: null, form: "Форма6"),
        };

        var result = GostPageGrouper.Group(pages);

        var group = Assert.Single(result.Documents);
        Assert.Equal("05-АР", group.Code);
        Assert.Equal([0, 1, 2], group.PageIndices);
        Assert.Equal("3", group.Fields["КоличествоЛистов"]);
        Assert.Equal("Пояснительная записка", group.Fields["НаименованиеДокумента"]);
    }

    /// <summary>
    /// Форма 6 ВСЕГДА продолжает текущую группу, даже если Шифр на ней радикально отличается от
    /// первого листа (шум распознавания мелкого штампа продолжения — не признак нового документа).
    /// </summary>
    [Fact]
    public void Form6Continuation_AlwaysJoinsCurrentGroup_EvenWithConflictingShifr()
    {
        var pages = new[]
        {
            Page("Документ", shifr: "DP-0623-035-ЕЦДМ-ЭМ", documentName: "1ВРУ", form: "Форма3"),
            Page("Документ", shifr: "ДР-0623-035-ЕЦ.ДМ-ЭМ", documentName: null, form: "Форма6"),
        };

        var result = GostPageGrouper.Group(pages);

        var group = Assert.Single(result.Documents);
        Assert.Equal("DP-0623-035-ЕЦДМ-ЭМ", group.Code);
        Assert.Equal([0, 1], group.PageIndices);
        Assert.Equal("1ВРУ", group.Fields["НаименованиеДокумента"]);
    }

    /// <summary>
    /// Ключевой сценарий (реальные данные «25-04-063-ЭМ»): два СМЕЖНЫХ первых листа (форма 3) с
    /// ОДИНАКОВЫМ шифром альбома — это РАЗНЫЕ документы («ЩО...» и «Схема системы уравнивания...»),
    /// должны остаться в отдельных группах, а не слиться из-за совпадения шифра.
    /// </summary>
    [Fact]
    public void TwoAdjacentFirstSheets_SameShifr_StaySeparate()
    {
        var pages = new[]
        {
            Page("Документ", shifr: "DP-ЕЦДМ-ЭМ", documentName: "ЩО. Схема...", form: "Форма3"),
            Page("Документ", shifr: "DP-ЕЦДМ-ЭМ", documentName: null, form: "Форма3"), // имя не распозналось → отдельная группа
        };

        var result = GostPageGrouper.Group(pages);

        Assert.Equal(2, result.Documents.Count);
        Assert.Equal([0], result.Documents[0].PageIndices);
        Assert.Equal([1], result.Documents[1].PageIndices);
    }

    /// <summary>
    /// «Общие данные» (форма 3, лист 1 альбома, без собственного НаименованиеДокумента) и «1ВРУ»
    /// (форма 3, лист 2) с общим шифром альбома — РАЗНЫЕ документы, каждый в своей группе.
    /// </summary>
    [Fact]
    public void GeneralDataThenSchema_SameAlbumShifr_AreSeparateGroups()
    {
        var pages = new[]
        {
            Page("Документ", shifr: "DP-ЕЦДМ-ЭМ", documentName: null, form: "Форма3"), // «Общие данные»
            Page("Документ", shifr: "DP-ЕЦДМ-ЭМ", documentName: "1ВРУ. Схема...", form: "Форма3"),
        };

        var result = GostPageGrouper.Group(pages);

        Assert.Equal(2, result.Documents.Count);
        Assert.Equal([0], result.Documents[0].PageIndices);
        Assert.Equal([1], result.Documents[1].PageIndices);
        Assert.Equal("1ВРУ. Схема...", result.Documents[1].Fields["НаименованиеДокумента"]);
    }

    /// <summary>
    /// На листах формы 6 НаименованиеДокумента принудительно обнуляется (по ГОСТ его там нет; модель
    /// иногда берёт под это поле строку из содержимого/таблицы листа). Имя группы — с первого листа.
    /// </summary>
    [Fact]
    public void Form6DocumentName_IsDroppedNotLeakedIntoGroup()
    {
        var pages = new[]
        {
            Page("Документ", shifr: "СО", documentName: "Спецификация оборудования", form: "Форма5"),
            Page("Документ", shifr: "СО", documentName: "Прокат цветных металлов", form: "Форма6"), // строка таблицы, не имя документа
        };

        var result = GostPageGrouper.Group(pages);

        var group = Assert.Single(result.Documents);
        Assert.Equal([0, 1], group.PageIndices);
        Assert.Equal("Спецификация оборудования", group.Fields["НаименованиеДокумента"]);
    }

    /// <summary>
    /// Форма 3/4/5 (первые листы разных категорий) — граница по смене НАИМЕНОВАНИЯ: разные имена при
    /// одном шифре → разные документы.
    /// </summary>
    [Theory]
    [InlineData("Форма3")]
    [InlineData("Форма4")]
    [InlineData("Форма5")]
    public void FirstSheetForms_DifferentNames_StartNewGroup(string form)
    {
        var pages = new[]
        {
            Page("Документ", shifr: "01-ЭМ", documentName: "А", form: form),
            Page("Документ", shifr: "01-ЭМ", documentName: "Б", form: form), // тот же шифр, другое имя → новый документ
        };

        var result = GostPageGrouper.Group(pages);

        Assert.Equal(2, result.Documents.Count);
    }

    /// <summary>
    /// Многолистовой чертёж, у которого каждый лист проштампован формой 3 (не формой 6), но Шифр И
    /// Наименование совпадают — один документ, листы сливаются. Это сценарий «241101-...-Карла
    /// Маркса» ПОСЛЕ того, как извлечение точного текста штампа сделало Шифр стабильным (раньше
    /// «241101 - ЭМ»↔«241101 - ЭОМ» из-за OCR-шума разбивал их).
    /// </summary>
    [Fact]
    public void AdjacentFirstSheets_SameShifrAndName_MergeIntoOneGroup()
    {
        var pages = new[]
        {
            Page("Документ", shifr: "241101 - ЭОМ", documentName: "План монтажа осветительных приборов", form: "Форма3"),
            Page("Документ", shifr: "241101 - ЭОМ", documentName: "План монтажа осветительных приборов", form: "Форма3"),
        };

        var result = GostPageGrouper.Group(pages);

        var group = Assert.Single(result.Documents);
        Assert.Equal([0, 1], group.PageIndices);
        Assert.Equal("2", group.Fields["КоличествоЛистов"]);
    }

    /// <summary>
    /// Строгое правило: два листа формы 3 с одинаковым Наименованием, но РАЗНЫМ Шифром — разные
    /// документы (Шифр снова участвует в границе, т.к. стал надёжным). Отличие Шифра ⇒ новая группа.
    /// </summary>
    [Fact]
    public void FirstSheets_SameName_DifferentShifr_StaySeparate()
    {
        var pages = new[]
        {
            Page("Документ", shifr: "01-ЭМ", documentName: "План", form: "Форма3"),
            Page("Документ", shifr: "02-ЭМ", documentName: "План", form: "Форма3"),
        };

        var result = GostPageGrouper.Group(pages);

        Assert.Equal(2, result.Documents.Count);
    }

    /// <summary>
    /// Форма не распознана (null) — трактуется как первый лист (форма 3): граница по смене
    /// наименования. Разные имена → разные группы.
    /// </summary>
    [Fact]
    public void UnknownForm_TreatedAsFirstSheet_BoundaryByName()
    {
        var result = GostPageGrouper.Group([Page("Документ", "01-ЭМ", "А"), Page("Документ", "01-ЭМ", "Б")]);

        Assert.Equal(2, result.Documents.Count);
    }

    /// <summary>
    /// По ГОСТ форма 6 не может быть первым листом документа. Страница не теряется: открывает
    /// группу-маркер аномалии «Некорректная форма 6».
    /// </summary>
    [Fact]
    public void Form6AsFirstDocumentPage_OpensIncorrectForm6SentinelGroup()
    {
        var result = GostPageGrouper.Group([Page("Документ", shifr: "01-ЭМ", documentName: null, form: "Форма6")]);

        var group = Assert.Single(result.Documents);
        Assert.Equal("Некорректная форма 6", group.Fields["НаименованиеДокумента"]);
    }
}
