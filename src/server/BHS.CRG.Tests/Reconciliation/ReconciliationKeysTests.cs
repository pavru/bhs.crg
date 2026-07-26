using BHS.CRG.Infrastructure.Reconciliation;

namespace BHS.CRG.Tests.Reconciliation;

/// <summary>
/// Доменный ключ — высший продуктовый риск подсистемы (P2 в issue #414). Разойдись нормализация — одна
/// позиция превратится в две находки-сироты, и журнал вместо памяти начнёт производить шум.
/// </summary>
public class ReconciliationKeysTests
{
    [Theory]
    // Одна и та же марка кабеля, записанная по-разному в разных документах, обязана дать один ключ.
    [InlineData("ВВГнг(А)-LS 3х2,5", "ВВГнг(А)–LS  3Х2.5")]
    [InlineData("  Кабель ВВГ  ", "кабель ввг")]
    [InlineData("Лоток 200×50", "лоток 200×50")]
    public void SameSubstance_SameKey(string a, string b)
        => Assert.Equal(ReconciliationKeys.NormalizePart(a), ReconciliationKeys.NormalizePart(b));

    [Theory]
    [InlineData("ВВГнг 3х2,5", "ВВГнг 3х4")]
    [InlineData("лоток 200", "лоток 300")]
    public void DifferentSubstance_DifferentKey(string a, string b)
        => Assert.NotEqual(ReconciliationKeys.NormalizePart(a), ReconciliationKeys.NormalizePart(b));

    /// <summary>
    /// Смешанная раскладка — не выдуманный случай: в рабочем кабельном журнале «ВВГнг(A)-LS» с
    /// латинской A и «ВВГнг(А)-LS» с кириллической стоят в соседних строках. Без сведения омоглифов
    /// одна марка дала бы две находки-сироты.
    /// </summary>
    [Theory]
    [InlineData("ВВГнг(A)-LS", "ВВГнг(А)-LS")]   // A латинская против кириллической
    [InlineData("КВВГ", "KBBГ")]                 // К и В латинские
    [InlineData("ПвПу2г", "ПвПy2г")]             // y латинская
    public void MixedLayout_FoldsToSameKey(string latin, string cyrillic)
        => Assert.Equal(ReconciliationKeys.NormalizePart(latin), ReconciliationKeys.NormalizePart(cyrillic));

    [Fact]
    public void CompositeKey_DoesNotCollideAcrossColumnBoundary()
    {
        // Наивная склейка через видимый разделитель дала бы одинаковый ключ для («АБ», «В») и («А», «БВ»).
        Assert.NotEqual(
            ReconciliationKeys.Build(["АБ", "В"]),
            ReconciliationKeys.Build(["А", "БВ"]));
    }

    [Fact]
    public void EmptyKey_IsRecognized_AndNotConfusedWithFilledOne()
    {
        Assert.True(ReconciliationKeys.IsEmpty(ReconciliationKeys.Build([null, "  "])));
        Assert.False(ReconciliationKeys.IsEmpty(ReconciliationKeys.Build(["ВВГ", ""])));
    }

    /// <summary>
    /// Ключ строится из СОДЕРЖАНИЯ, поэтому не зависит от положения строки. Это и есть защита от P2:
    /// перенумерация 1..N в этих документах происходит регулярно.
    /// </summary>
    [Fact]
    public void Key_IsIndependentOfRowPosition()
    {
        var first = ReconciliationKeys.Build(["ВВГнг(А)-LS", "3х2,5"]);
        var afterRenumbering = ReconciliationKeys.Build(["ВВГнг(А)-LS", "3х2,5"]);
        Assert.Equal(first, afterRenumbering);
    }
}
