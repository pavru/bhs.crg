using BHS.CRG.Tests.Support;

namespace BHS.CRG.Tests.Recognition;

/// <summary>
/// Хеши заводского содержимого встроенных профилей прибиты числами (issue #1075).
///
/// <para>По хешу сидер решает, ушёл ли заводской профиль вперёд, пока администратор держит свою
/// правку (<c>RecognitionProfile.BuiltInHash</c>). Перенос объявления профиля из одного места в
/// другое — в модуль, в другой тип записи — содержимого не меняет, но хеш считается по сериализованной
/// записи, и разойтись он может от чего угодно: порядка свойств, умолчания типа, пустого списка вместо
/// отсутствующего. Тогда у КАЖДОГО заказчика, правившего профиль, после обновления загорится
/// «заводской профиль обновился», хотя не обновилось ничего.</para>
///
/// <para>⚠️ Менять число здесь можно только вместе с настоящей правкой содержимого профиля — поля,
/// описания, названия. Если тест покраснел от переноса или переименования, чинить надо перенос.</para>
/// </summary>
public class BuiltInProfileHashPinTests
{
    [Theory]
    [InlineData("titleblock", "A0F3A3BC1A26E2EFE1B7AFED6059C0EC2060F0E263C0FE00B8FC27086B7DE943")]
    [InlineData("cover-title", "C27311682DE9E3E92D2823B1833AF95E9729AFABAD35FA0BEBE7B1B7E1707D51")]
    [InlineData("invoice", "5FF9C0341A9ADEC855BDE0197F71FAEB09ED9F99E0D4C3491F1715EF81D926FB")]
    [InlineData("spec-table", "FAA89A9DCF1E130D9A3ADCDB8BF76342724ECE41CFC6D8315230138469315E43")]
    [InlineData("cable-journal", "95FC222F0967378ACB383F4D72D840DCD7BCD7E9475D8C2F089CC8D0362CBF52")]
    public void Factory_hash_is_pinned(string code, string expected)
    {
        // Числа сняты с кода ДО переноса объявлений в модули — со статического списка ядра.
        var def = TestRecognition.Catalog.All.Single(d => d.Code == code);

        Assert.Equal(expected, def.Hash);
    }
}
