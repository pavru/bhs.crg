using BHS.CRG.Api.Configuration;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Проверка ключа подписи на старте. Смысл проверки — не дать приложению подняться на значении,
/// которое развёртывание не задавало: незаданном, слишком коротком или взятом из файла-примера.
/// </summary>
public class JwtKeyGuardTests
{
    private const string Good = "9f2c4a7e1b8d3056af12cd94be77e0a3d5f68219c4b7ae30";

    [Fact]
    public void GoodKey_Passes()
        => Assert.Equal(Good, JwtKeyGuard.Require(Good));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingKey_Rejected(string? key)
        => Assert.Throws<InvalidOperationException>(() => JwtKeyGuard.Require(key));

    [Fact]
    public void ShortKey_Rejected()
        => Assert.Throws<InvalidOperationException>(() => JwtKeyGuard.Require(new string('a', 31)));

    [Fact]
    public void ExactlyMinimumLength_Passes()
        => Assert.Equal(new string('a', 32), JwtKeyGuard.Require(new string('a', 32)));

    /// <summary>
    /// Длина считается в БАЙТАХ, а не в символах: 31 кириллическая буква — это 62 байта и ключ
    /// достаточной длины, тогда как посимвольная проверка отвергла бы его.
    /// </summary>
    [Fact]
    public void CyrillicKey_MeasuredInBytes()
        => Assert.Equal(new string('я', 31), JwtKeyGuard.Require(new string('я', 31)));

    /// <summary>
    /// Значения из файлов-примеров длиннее минимума — именно поэтому проверка длины их не ловит и
    /// нужна отдельная. Проверяем и частично поправленный вариант: узнаваемое начало осталось.
    /// </summary>
    [Theory]
    [InlineData("CHANGE_ME_TO_A_LONG_RANDOM_SECRET_KEY_32CHARS")]
    [InlineData("CHANGE_ME_TO_A_LONG_RANDOM_SECRET_AT_LEAST_32_CHARS")]
    [InlineData("change_me_but_i_only_edited_the_tail_part_here_ok")]
    [InlineData("your-secret-key-goes-here-and-is-long-enough-yes")]
    public void PlaceholderKey_Rejected(string key)
    {
        Assert.True(key.Length >= 32, "проверяем именно то, что длина такой ключ не отсекает");
        Assert.Throws<InvalidOperationException>(() => JwtKeyGuard.Require(key));
    }

    /// <summary>
    /// Список узнаваемых начал не должен ловить обычные слова: ключ, выбранный человеком и просто
    /// начинающийся со слова «secret», — настоящий, и отказ с причиной «значение из файла-примера»
    /// отправил бы искать несуществующую проблему.
    /// </summary>
    [Theory]
    [InlineData("secretkey-prod-2026-9f2c4a7e1b8d3056af12cd94")]
    [InlineData("SecretPassphraseChosenByAnOperator-8b31d0c4")]
    public void OrdinaryKeyStartingWithCommonWord_Passes(string key)
        => Assert.Equal(key, JwtKeyGuard.Require(key));

    /// <summary>Отказ обязан говорить, что делать: сообщение читает тот, кто разворачивает.</summary>
    [Fact]
    public void Message_TellsWhatToDo()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => JwtKeyGuard.Require(null));
        Assert.Contains("Jwt__Key", ex.Message);
        Assert.Contains("openssl", ex.Message);
    }
}
