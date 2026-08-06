using Npgsql;

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
    public static void Require(string? connectionString, string? accessKey, string? secretKey, string? bucket)
    {
        RequireConnectionString(connectionString);
        RequireStorageValue(accessKey, "BlobStorage__AccessKey", "MINIO_ROOT_USER");
        RequireStorageValue(secretKey, "BlobStorage__SecretKey", "MINIO_ROOT_PASSWORD");
        // Бакет — третья дверь в том же ряду. Compose подставляет его так же (`${MINIO_BUCKET}`, без
        // значения по умолчанию), пустое значение так же перекрывает дефолт C#, а узнаётся об этом
        // ещё позже: имя бакета трогается лениво, на первой загрузке файла. Проверять два значения
        // из трёх значило бы оставить ровно тот отказ, ради устранения которого проверка и заведена.
        RequireStorageValue(bucket, "BlobStorage__Bucket", "MINIO_BUCKET");
    }

    /// <summary>
    /// Строку подключения РАЗБИРАЕМ, а не смотрим на неё целиком.
    ///
    /// Проверка «не пусто» здесь почти бесполезна: развёртывание собирает строку по шаблону
    /// (<c>Host=postgres;…;Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}</c>), и незаданная
    /// переменная даёт не пустую строку, а строку с пустым полем — «Password=» в конце. Такое
    /// значение непусто, заглушки не содержит и прошло бы насквозь, а развёртыватель получил бы всё
    /// тот же отказ драйвера. То есть проверка была бы на словах.
    /// </summary>
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

        NpgsqlConnectionStringBuilder parsed;
        try
        {
            parsed = new NpgsqlConnectionStringBuilder(value);
        }
        catch (Exception ex)
        {
            throw Fail(
                $"Строка подключения к базе данных (ConnectionStrings:Postgres) не разбирается: {ex.Message}",
                "ConnectionStrings__Postgres",
                "в развёртывании compose она собирается из POSTGRES_* в deploy/.env");
        }

        RequirePart(parsed.Host, "адрес сервера (Host)", "POSTGRES_* и адрес сервера БД");
        RequirePart(parsed.Database, "имя базы (Database)", "POSTGRES_DB");
        RequirePart(parsed.Username, "имя пользователя (Username)", "POSTGRES_USER");
        // Пароль требуем наравне с остальным. Postgres умеет пускать и без него (trust, .pgpass), но
        // наша поставка так не разворачивается: compose передаёт пароль переменной, DEPLOYMENT.md
        // требует его сменить, а СУБД, доступная по сети без пароля, — сама по себе дефект. Если
        // однажды понадобится иной способ входа, это отдельное решение, а не молчаливая щель.
        RequirePart(parsed.Password, "пароль (Password)", "POSTGRES_PASSWORD");
    }

    private static void RequirePart(string? value, string what, string composeName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw Fail(
                $"В строке подключения к базе данных (ConnectionStrings:Postgres) не заполнено: {what}.",
                "ConnectionStrings__Postgres",
                $"в развёртывании compose это {composeName} в deploy/.env");
    }

    private static void RequireStorageValue(string? value, string envName, string composeName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw Fail(
                $"Настройка хранилища файлов ({envName.Replace("__", ":")}) не задана.",
                envName,
                $"в развёртывании compose это {composeName} в deploy/.env");

        if (ConfigPlaceholders.LooksLikeExample(value))
            throw Fail(
                $"Настройка хранилища файлов ({envName.Replace("__", ":")}) совпадает со " +
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
