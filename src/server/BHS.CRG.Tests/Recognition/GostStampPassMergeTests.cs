using BHS.CRG.Infrastructure.Recognition;

namespace BHS.CRG.Tests.Recognition;

public class GostStampPassMergeTests
{
    [Fact]
    public void Pass2_OverridesPass1_OnConflictingField()
    {
        // Пасс-1 (вся страница) ошибся в шифре, пасс-2 (кроп в высоком разрешении) прочитал верно.
        var full = new Dictionary<string, string?> { ["Шифр"] = "DP-0623-035-ЕЦ.ДМ-ЭМ" };
        var crop = new Dictionary<string, string?> { ["Шифр"] = "DP-0623-035-ЕЦДМ-ЭМ" };

        var merged = GostStampPassMerge.Merge(full, crop);

        Assert.Equal("DP-0623-035-ЕЦДМ-ЭМ", merged["Шифр"]);
    }

    [Fact]
    public void Pass1FieldAbsentInPass2_IsKept()
    {
        // Кроп физически не захватил графу / классификатор не запрашивался во втором проходе —
        // значение остаётся из пасс-1.
        var full = new Dictionary<string, string?>
        {
            ["Шифр"] = "01-ЭМ",
            ["ТипСтраницы"] = "Документ",
            ["Форма"] = "Форма3",
            ["Масштаб"] = "1:100",
        };
        var crop = new Dictionary<string, string?> { ["Шифр"] = "01-ЭМ" };

        var merged = GostStampPassMerge.Merge(full, crop);

        Assert.Equal("Документ", merged["ТипСтраницы"]);
        Assert.Equal("Форма3", merged["Форма"]);
        Assert.Equal("1:100", merged["Масштаб"]);
    }

    [Fact]
    public void Pass2EmptyValue_DoesNotOverwritePass1()
    {
        var full = new Dictionary<string, string?> { ["НаименованиеДокумента"] = "План силовой сети" };
        var crop = new Dictionary<string, string?> { ["НаименованиеДокумента"] = "" };

        var merged = GostStampPassMerge.Merge(full, crop);

        Assert.Equal("План силовой сети", merged["НаименованиеДокумента"]);
    }

    [Fact]
    public void Pass2NewField_IsAdded()
    {
        // Кроп прочитал поле, которого пасс-1 не осилил (напр. НаименованиеДокумента для «1ВРУ»).
        var full = new Dictionary<string, string?> { ["Шифр"] = "DP-ЕЦДМ-ЭМ" };
        var crop = new Dictionary<string, string?> { ["НаименованиеДокумента"] = "1ВРУ. Схема..." };

        var merged = GostStampPassMerge.Merge(full, crop);

        Assert.Equal("1ВРУ. Схема...", merged["НаименованиеДокумента"]);
        Assert.Equal("DP-ЕЦДМ-ЭМ", merged["Шифр"]);
    }
}
