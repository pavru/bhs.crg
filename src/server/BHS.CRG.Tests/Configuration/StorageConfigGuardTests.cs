using BHS.CRG.Api.Configuration;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Обязательные значения конфигурации останавливают запуск, если не заданы (issue #706).
///
/// Смысл проверки — однородность отказа. Ключ подписи падал на старте, а строка подключения и
/// учётные данные хранилища доходили до первого обращения и отвечали чужими словами: «Host can't be
/// null» от драйвера, имя учётной записи от клиента MinIO. Поднимающий систему узнавал о незаданном
/// в разное время и в разном виде.
/// </summary>
public class StorageConfigGuardTests
{
    private const string GoodConnection = "Host=postgres;Port=5432;Database=bhs_crg;Username=postgres;Password=s3cret";

    [Fact]
    public void FullConfiguration_Passes()
        => StorageConfigGuard.Require(GoodConnection, "storage-user", "storage-password");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingConnectionString_StopsStartup(string? connection)
        => Assert.Throws<InvalidOperationException>(
            () => StorageConfigGuard.Require(connection, "storage-user", "storage-password"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingAccessKey_StopsStartup(string? accessKey)
        => Assert.Throws<InvalidOperationException>(
            () => StorageConfigGuard.Require(GoodConnection, accessKey, "storage-password"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingSecretKey_StopsStartup(string? secretKey)
        => Assert.Throws<InvalidOperationException>(
            () => StorageConfigGuard.Require(GoodConnection, "storage-user", secretKey));

    /// <summary>
    /// Заглушки из <c>deploy/.env.example</c> — ровно те строки, что развёртывание копирует и
    /// забывает поправить. В учётных данных они стоят целым значением.
    /// </summary>
    [Theory]
    [InlineData("change_me_storage_admin")]
    [InlineData("CHANGE_ME_TO_SOMETHING")]
    [InlineData("changeme")]
    public void PlaceholderCredential_StopsStartup(string value)
        => Assert.Throws<InvalidOperationException>(
            () => StorageConfigGuard.Require(GoodConnection, value, "storage-password"));

    /// <summary>
    /// А в строке подключения заглушка сидит В СЕРЕДИНЕ: пароль подставляется развёртыванием
    /// внутрь готовой строки. Проверка по началу строки такое пропустила бы.
    /// </summary>
    [Fact]
    public void PlaceholderPasswordInsideConnectionString_StopsStartup()
        => Assert.Throws<InvalidOperationException>(() => StorageConfigGuard.Require(
            "Host=postgres;Port=5432;Database=bhs_crg;Username=postgres;Password=change_me_strong_password",
            "storage-user", "storage-password"));

    /// <summary>
    /// Общеизвестные учётные данные пропускаем НАМЕРЕННО: на них работают и локальная разработка, и
    /// контейнер MinIO из dev-окружения. Отказ, зависящий от среды, означал бы, что боевое поведение
    /// в разработке никогда не выполняется — предупреждение об этом стоит в файле-примере.
    /// </summary>
    [Fact]
    public void WellKnownDevelopmentCredentials_Pass()
        => StorageConfigGuard.Require(GoodConnection, "minioadmin", "minioadmin");

    /// <summary>Отказ читает тот, кто разворачивает, — в логе контейнера, где спросить некого.</summary>
    [Fact]
    public void Message_NamesTheVariableAndWhereItComesFrom()
    {
        var db = Assert.Throws<InvalidOperationException>(
            () => StorageConfigGuard.Require(null, "storage-user", "storage-password"));
        Assert.Contains("ConnectionStrings__Postgres", db.Message);
        Assert.Contains("POSTGRES_PASSWORD", db.Message);

        var storage = Assert.Throws<InvalidOperationException>(
            () => StorageConfigGuard.Require(GoodConnection, null, "storage-password"));
        Assert.Contains("BlobStorage__AccessKey", storage.Message);
        Assert.Contains("MINIO_ROOT_USER", storage.Message);
    }
}
