namespace BHS.CRG.Api.Configuration;

/// <summary>
/// Проверка обязательных значений конфигурации, без которых приложение работать не может: строки
/// подключения к БД и учётных данных хранилища.
///
/// Зачем отдельная проверка, если без них всё равно не работает. Затем, что «не работает»
/// приходило поздно и не тем: пустые значения не оставляют дефолты C# в силе, а перекрывают их
/// пустой строкой (см. <c>appsettings.json</c>, где эти ключи объявлены пустыми), и развёртывание
/// поднималось молча. Про хранилище человек узнавал при первой загрузке файла — сообщением клиента
/// MinIO, называющим учётную запись; про БД — фразой драйвера «Host can't be null» из середины
/// стека. Ни то ни другое не говорит, ЧТО задать.
///
/// Режим отказа стал вдобавок неоднородным: ключ подписи с версии 0.88.0 останавливает запуск
/// (<see cref="JwtKeyGuard" />), а два соседних обязательных значения — нет. Разнобой хуже любого
/// из двух поведений: поднимающий систему по инструкции узнаёт о незаданном в разное время и
/// разными словами.
///
/// Чего проверка НЕ делает: не судит о качестве значения. Общеизвестные учётные данные хранилища
/// (minioadmin) она пропускает — на них работает и локальная разработка, и контейнер MinIO из
/// dev-окружения, а отказ, зависящий от среды, означал бы, что боевое поведение в разработке
/// никогда не выполняется. Предупреждение об этом стоит в <c>deploy/.env.example</c>, там ему и
/// место.
/// </summary>
public static class StorageConfigGuard
{
    /// <summary>
    /// Проверяет всё разом и бросает при первом негодном значении.
    /// </summary>
    /// <param name="connectionString">Строка подключения к PostgreSQL (<c>ConnectionStrings:Postgres</c>).</param>
    /// <param name="accessKey">Ключ доступа к хранилищу (<c>BlobStorage:AccessKey</c>).</param>
    /// <param name="secretKey">Секретный ключ хранилища (<c>BlobStorage:SecretKey</c>).</param>
    public static void Require(string? connectionString, string? accessKey, string? secretKey)
    {
        RequireConnectionString(connectionString);
        RequireStorageCredential(accessKey, "BlobStorage__AccessKey", "MINIO_ROOT_USER");
        RequireStorageCredential(secretKey, "BlobStorage__SecretKey", "MINIO_ROOT_PASSWORD");
    }

    private static void RequireConnectionString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw Fail(
                "Строка подключения к базе данных (ConnectionStrings:Postgres) не задана.",
                "ConnectionStrings__Postgres",
                "в развёртывании compose она собирается из POSTGRES_DB / POSTGRES_USER / " +
                "POSTGRES_PASSWORD в deploy/.env");

        // Заглушку ищем ВНУТРИ строки: пароль подставляется в её середину, и проверка по началу
        // пропустила бы «Host=postgres;…;Password=change_me_strong_password».
        if (ConfigPlaceholders.ContainsExample(value))
            throw Fail(
                "Строка подключения к базе данных (ConnectionStrings:Postgres) содержит значение из " +
                "файла-примера.",
                "ConnectionStrings__Postgres",
                "задайте собственный пароль в POSTGRES_PASSWORD (deploy/.env)");
    }

    private static void RequireStorageCredential(string? value, string envName, string composeName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw Fail(
                $"Учётные данные хранилища файлов ({envName.Replace("__", ":")}) не заданы.",
                envName,
                $"в развёртывании compose это {composeName} в deploy/.env");

        if (ConfigPlaceholders.LooksLikeExample(value))
            throw Fail(
                $"Учётные данные хранилища файлов ({envName.Replace("__", ":")}) совпадают со " +
                "значением из файла-примера.",
                envName,
                $"задайте собственное значение в {composeName} (deploy/.env)");
    }

    /// <summary>
    /// Тон тот же, что у <see cref="JwtKeyGuard" />: что не так, что приложение не запущено
    /// намеренно и чем именно задаётся значение. Читает это тот, кто разворачивает систему, — и
    /// читает в логе контейнера, где подсказки взять больше негде.
    /// </summary>
    private static InvalidOperationException Fail(string what, string envName, string how) => new(
        $"{what} Приложение не запущено намеренно.\n" +
        $"Задайте значение переменной окружения {envName} либо в конфигурации среды — {how}.");
}
