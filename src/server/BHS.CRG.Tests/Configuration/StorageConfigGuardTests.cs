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
        => StorageConfigGuard.Require(GoodConnection, "storage-user", "storage-password", "bhs-crg");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingConnectionString_StopsStartup(string? connection)
        => Assert.Throws<InvalidOperationException>(
            () => StorageConfigGuard.Require(connection, "storage-user", "storage-password", "bhs-crg"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingAccessKey_StopsStartup(string? accessKey)
        => Assert.Throws<InvalidOperationException>(
            () => StorageConfigGuard.Require(GoodConnection, accessKey, "storage-password", "bhs-crg"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingSecretKey_StopsStartup(string? secretKey)
        => Assert.Throws<InvalidOperationException>(
            () => StorageConfigGuard.Require(GoodConnection, "storage-user", secretKey, "bhs-crg"));

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
            () => StorageConfigGuard.Require(GoodConnection, value, "storage-password", "bhs-crg"));

    /// <summary>
    /// А в строке подключения заглушка сидит В СЕРЕДИНЕ: пароль подставляется развёртыванием
    /// внутрь готовой строки. Проверка по началу строки такое пропустила бы.
    /// </summary>
    [Fact]
    public void PlaceholderPasswordInsideConnectionString_StopsStartup()
        => Assert.Throws<InvalidOperationException>(() => StorageConfigGuard.Require(
            "Host=postgres;Port=5432;Database=bhs_crg;Username=postgres;Password=change_me_strong_password",
            "storage-user", "storage-password", "bhs-crg"));

    /// <summary>
    /// Общеизвестные учётные данные пропускаем НАМЕРЕННО: на них работают и локальная разработка, и
    /// контейнер MinIO из dev-окружения. Отказ, зависящий от среды, означал бы, что боевое поведение
    /// в разработке никогда не выполняется — предупреждение об этом стоит в файле-примере.
    /// </summary>
    [Fact]
    public void WellKnownDevelopmentCredentials_Pass()
        => StorageConfigGuard.Require(GoodConnection, "minioadmin", "minioadmin", "bhs-crg");

    /// <summary>
    /// Бакет — третья дверь в том же ряду, и подставляется так же (`${MINIO_BUCKET}` без значения
    /// по умолчанию). Проверять два значения из трёх значило бы оставить ровно тот поздний отказ,
    /// ради устранения которого проверка и заведена: имя бакета трогается лениво, на первой
    /// загрузке файла.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("change_me_bucket")]
    public void MissingOrPlaceholderBucket_StopsStartup(string? bucket)
        => Assert.Throws<InvalidOperationException>(
            () => StorageConfigGuard.Require(GoodConnection, "storage-user", "storage-password", bucket));

    /// <summary>
    /// Развёртывание собирает строку подключения по шаблону, и незаданная переменная даёт не пустую
    /// строку, а строку с пустым полем. Проверка «не пусто» такое пропускает — а обещание в
    /// DEPLOYMENT.md касается именно этого случая, единственного, с которым и сталкиваются.
    /// </summary>
    [Theory]
    [InlineData("Host=postgres;Port=5432;Database=bhs_crg;Username=postgres;Password=")]
    [InlineData("Host=postgres;Port=5432;Database=;Username=postgres;Password=s3cret")]
    [InlineData("Host=postgres;Port=5432;Database=bhs_crg;Username=;Password=s3cret")]
    [InlineData("Port=5432;Database=bhs_crg;Username=postgres;Password=s3cret")]
    public void ConnectionStringWithEmptyPart_StopsStartup(string connection)
        => Assert.Throws<InvalidOperationException>(
            () => StorageConfigGuard.Require(connection, "storage-user", "storage-password", "bhs-crg"));

    /// <summary>Нечитаемая строка — тоже отказ на старте, а не исключение драйвера позже.</summary>
    [Fact]
    public void UnparsableConnectionString_StopsStartup()
        => Assert.Throws<InvalidOperationException>(() => StorageConfigGuard.Require(
            "это вообще не строка подключения", "storage-user", "storage-password", "bhs-crg"));

    /// <summary>Отказ читает тот, кто разворачивает, — в логе контейнера, где спросить некого.</summary>
    [Fact]
    public void Message_NamesTheVariableAndWhereItComesFrom()
    {
        var db = Assert.Throws<InvalidOperationException>(
            () => StorageConfigGuard.Require(null, "storage-user", "storage-password", "bhs-crg"));
        Assert.Contains("ConnectionStrings__Postgres", db.Message);
        Assert.Contains("POSTGRES_PASSWORD", db.Message);

        var storage = Assert.Throws<InvalidOperationException>(
            () => StorageConfigGuard.Require(GoodConnection, null, "storage-password", "bhs-crg"));
        Assert.Contains("BlobStorage__AccessKey", storage.Message);
        Assert.Contains("MINIO_ROOT_USER", storage.Message);
    }
}
