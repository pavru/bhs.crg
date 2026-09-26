using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Миграция не теряет справочник ядра — на ПЕРЕСОЗДАННОЙ базе и на базе С ИСТОРИЕЙ
/// (ТЗ CORE-29/CORE-31, issue #960).
///
/// <para>Две базы, а не одна, и это не педантизм: пересозданная приходит к последней миграции одним
/// прыжком, а у заказчика база проходит их подряд, поверх данных, накопленных прежними версиями.
/// Различает их порядок создания — и всё, что от него зависит: значения, проставленные прежними
/// умолчаниями, строки, которых в свежей схеме не бывает, и индексы, которые на пустой таблице
/// создаются всегда, а на полной могут и не создаться.</para>
///
/// <para>С issue #963 здесь же проверяется ПЕРЕШИВКА ТИПОВ: «Номенклатура» обязана встать НАД
/// «Материалом», а не рядом с ним. Проверка на тех же двух базах и по той же причине — перешивка
/// живых типов задевает документы, которые на них ссылаются.</para>
///
/// <para>⚠️ Базы свои, а не общая тестовая: тест применяет миграции ЧАСТЯМИ и создаёт схему с нуля,
/// то есть делает с базой то, чего соседние тесты не переживут.</para>
/// </summary>
[Collection("Integration")]
public class MigrationCensusTests
{
    [Fact]
    public async Task Fresh_database_migrates_and_the_census_has_nothing_to_compare()
    {
        await using var db = await CreateDatabaseAsync("bhs_crg_census_fresh");

        // База пустая: таблиц нет, сверять не с чем — и это НЕ повод для отказа старта.
        var before = await MigrationCensus.ReadAsync(db);
        Assert.Null(before);

        await db.Database.MigrateAsync();

        var after = await MigrationCensus.ReadAsync(db);
        Assert.NotNull(after);
        Assert.Equal(0, after.Counts["строек"]);
        MigrationCensus.EnsureUnchanged(before, after);   // не бросает
    }

    /// <summary>
    /// ЧИСТАЯ УСТАНОВКА: базы ещё нет вовсе, её создаст первая миграция. Перепись обязана молча
    /// вернуть «нечего сверять», а не уронить старт.
    ///
    /// <para>Написан по следам отказа CI на PR #1046: сторож, поставленный ПЕРЕД миграцией, падал с
    /// «database does not exist» — то есть install.sh у заказчика не поднялся бы вовсе. Локально
    /// этого не видно никогда: база разработчика существует всегда, и проверка проходит вхолостую.</para>
    /// </summary>
    [Fact]
    public async Task Missing_database_is_not_a_failure_it_is_the_first_start()
    {
        var name = "bhs_crg_census_absent";
        await using (var conn = new NpgsqlConnection(AdminConnectionString()))
        {
            await conn.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", conn);
            await drop.ExecuteNonQueryAsync();
        }

        await using var db = new AppDbContext(OptionsFor(name));
        Assert.Null(await MigrationCensus.ReadAsync(db));
        MigrationCensus.EnsureUnchanged(null, null);   // и сверять нечего — не бросает
    }

    [Fact]
    public async Task Database_with_history_keeps_every_construction_section_and_set()
    {
        await using var db = await CreateDatabaseAsync("bhs_crg_census_history");

        // Схема ПРЕЖНЕЙ версии: доходим до предпоследней миграции — проверяем ту, что добавлена
        // последней, какой бы она ни была. Имя не прибито нарочно: прибитое устареет со следующей
        // миграцией, и тест продолжил бы проверять давно проверенное, молча.
        var all = db.Database.GetMigrations().ToList();
        var previous = all[^2];
        await db.GetService<IMigrator>().MigrateAsync(previous);

        // Данные, накопленные прежней версией: стройка → раздел → комплект → документ на комплекте.
        var construction = Guid.NewGuid();
        var section = Guid.NewGuid();
        var set = Guid.NewGuid();
        await ExecAsync(db, $"""
            INSERT INTO constructions ("Id","Name","CreatedByUserId","CreatedAt","UpdatedAt")
              VALUES ('{construction}','Стройка из прошлого','{Guid.NewGuid()}', now(), now());
            INSERT INTO sections ("Id","Name","ConstructionId","CreatedAt","UpdatedAt")
              VALUES ('{section}','Раздел','{construction}', now(), now());
            INSERT INTO document_sets ("Id","Name","SectionId","CreatedAt","UpdatedAt")
              VALUES ('{set}','Комплект','{section}', now(), now());
            """);

        var before = await MigrationCensus.ReadAsync(db);
        Assert.NotNull(before);
        Assert.Equal(1, before.Counts["строек"]);

        await db.Database.MigrateAsync();

        var after = await MigrationCensus.ReadAsync(db);
        MigrationCensus.EnsureUnchanged(before, after);

        // Колонки появились и пусты: «как у компании» — это отсутствие своего пояса, а не значение,
        // записанное миграцией в каждую строку (иначе смена пояса компании не тронула бы никого).
        Assert.Null(await ScalarAsync(db, $"SELECT \"TimeZoneId\" FROM constructions WHERE \"Id\" = '{construction}'"));
        Assert.Equal("Стройка из прошлого",
            await ScalarAsync(db, $"SELECT \"Name\" FROM constructions WHERE \"Id\" = '{construction}'"));
    }

    /// <summary>
    /// Сверка обязана ЛОВИТЬ потерю, а не только не мешать. Проверяется на переписях напрямую:
    /// подделать миграцию нельзя, а потеря выглядит именно так.
    /// </summary>
    [Fact]
    public void Census_refuses_when_something_disappeared()
    {
        static MigrationCensus Census(long constructions, long sections, long sets, long orphanSections) =>
            new(new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["строек"] = constructions,
                ["разделов"] = sections,
                ["комплектов"] = sets,
                ["разделов без своей стройки"] = orphanSections,
            });

        var before = Census(2, 5, 4, 0);

        var lost = Assert.Throws<InvalidOperationException>(
            () => MigrationCensus.EnsureUnchanged(before, Census(2, 4, 4, 0)));
        Assert.Contains("разделов: было 5, стало 4", lost.Message);

        // Ссылка обрублена: строки на месте, но раздел больше не находит свою стройку. По итогам
        // это невидимо — ловится только счётом сирот.
        var orphaned = Assert.Throws<InvalidOperationException>(
            () => MigrationCensus.EnsureUnchanged(before, Census(2, 5, 4, 3)));
        Assert.Contains("разделов без своей стройки: было 0, стало 3", orphaned.Message);

        var wiped = Assert.Throws<InvalidOperationException>(
            () => MigrationCensus.EnsureUnchanged(before, null));
        Assert.Contains("справочник исчез целиком", wiped.Message);

        // Строка ПРОПАЛА из переписи — таблицы больше нет, и дальше сторож ослеп бы молча.
        var blinded = Assert.Throws<InvalidOperationException>(() => MigrationCensus.EnsureUnchanged(
            before,
            new MigrationCensus(new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["строек"] = 2, ["разделов"] = 5, ["комплектов"] = 4,
            })));
        Assert.Contains("считать стало нечем", blinded.Message);

        // Новая строка в переписи — НЕ расхождение: миграция создала таблицу, которой не было.
        MigrationCensus.EnsureUnchanged(before, new MigrationCensus(
            before.Counts.Concat([new KeyValuePair<string, long>("объектов без своего уровня", 0)])
                .ToDictionary(StringComparer.Ordinal)));

        // А равные переписи проходят молча.
        MigrationCensus.EnsureUnchanged(before, Census(2, 5, 4, 0));
    }

    // ── Перешивка типов: номенклатура над материалом (issue #963) ─────────────

    /// <summary>
    /// Схема «Материала» в рабочей базе — списком, как она там лежит (сверено 26.09.2026 на копии
    /// базы заказчика и на базе разработки: обе совпадают до ключа).
    ///
    /// <para>Порядок тэгов <c>identity</c> здесь ВАЖЕН и неочевиден: наименование 1, производитель
    /// 2, артикул 3 — не как в примере ТЗ. Из них складывается ключ, которым строка накладной
    /// находит запись справочника; перенумеруй их миграция «по ТЗ» — разошлись бы существующие
    /// связки с документами качества, и заметили бы это не здесь.</para>
    /// </summary>
    private const string LegacyMaterialSchema = """
        {"fields":[
          {"key":"Группа","title":"Группа","type":"string"},
          {"key":"Наименование","title":"Наименование","type":"string","required":true,"tags":["identity:1"]},
          {"key":"Артикул","title":"Артикул","type":"string","tags":["identity:3"]},
          {"key":"Производитель","title":"Производитель","type":"string","tags":["identity:2","quality.manufacturer"]},
          {"key":"ЕдиницаИзмерения","title":"Единица измерения","type":"complex","required":true,"typeId":"@unit@"},
          {"key":"Количество","title":"Количество","type":"number","required":true},
          {"key":"Цена","title":"Цена","type":"number"},
          {"key":"ДокументПодтверждающийКачество","title":"Документ качества","type":"doc-ref","tags":["material.qualityDocLink"]},
          {"key":"СсылочнаяИнформация","title":"СсылочнаяИнформация","type":"string"},
          {"key":"Изображение","title":"Изображение","type":"image"}
        ],"typstRenders":[{"name":"Строка","fnName":"resource-name-quantity","block":"{ }"}]}
        """;

    private const string LegacyWorkSchema = """
        {"fields":[
          {"key":"Наименование","title":"Наименование","type":"string","required":true},
          {"key":"Количество","title":"Количество","type":"number","required":true},
          {"key":"ЕдиницаИзмерения","title":"Единица измерения","type":"complex","required":true,"typeId":"@unit@"}
        ]}
        """;

    /// <summary>
    /// СТОРОЖ ЗАДАЧИ (issue #963). На базе С ИСТОРИЕЙ: общие поля «Материала» уходят наверх, в новый
    /// базовый тип, а сам он становится производным. Второго справочника материалов не появляется —
    /// именно этим задача и ломается: заведи «Номенклатуру» рядом, и у заказчика два «материала».
    /// </summary>
    [Fact]
    public async Task История_поднимает_номенклатуру_над_материалом()
    {
        await using var db = await CreateDatabaseAsync("bhs_crg_census_lift");
        await MigrateToPreviousAsync(db);
        var unit = await SeedLegacyTypesAsync(db);

        var before = await MigrationCensus.ReadAsync(db);
        Assert.NotNull(before);
        Assert.Equal(10, before.Counts["полей у справочника номенклатуры"]);

        await db.Database.MigrateAsync();

        // Состав не изменился: поля переехали, а не потерялись.
        MigrationCensus.EnsureUnchanged(before, await MigrationCensus.ReadAsync(db));

        // Один справочник, а не два: «Материал» производен от «Номенклатуры».
        Assert.Equal(await ScalarAsync(db, "SELECT \"Id\"::text FROM document_types WHERE \"Code\" = 'Номенклатура'"),
            await ScalarAsync(db, "SELECT \"ParentId\"::text FROM document_types WHERE \"Code\" = 'Материал'"));

        // У строки остались количество и цена; всё остальное она наследует.
        Assert.Equal("Количество, Цена", await OwnKeysAsync(db, "Материал"));
        Assert.Equal(
            "Группа, Наименование, Артикул, Производитель, ЕдиницаИзмерения, "
            + "ДокументПодтверждающийКачество, СсылочнаяИнформация, Изображение",
            await OwnKeysAsync(db, "Номенклатура"));

        // Ссылка на единицы переехала как есть — с тем же идентификатором цели.
        Assert.Equal(unit.ToString(), await ScalarAsync(db,
            "SELECT f->>'typeId' FROM document_types t, jsonb_array_elements(t.\"Schema\"->'fields') f "
            + "WHERE t.\"Code\" = 'Номенклатура' AND f->>'key' = 'ЕдиницаИзмерения'"));

        // Поле документа качества — РОВНО В ОДНОЙ схеме. Окажись оно в двух, материалом стали бы два
        // типа, и ключ сопоставления собрался бы из полей обоих (MaterialIdentity.KeysOf).
        Assert.Equal(1L, await ScalarAsync(db,
            "SELECT count(*) FROM document_types t, jsonb_array_elements(t.\"Schema\"->'fields') f "
            + "WHERE coalesce(f->'tags', '[]'::jsonb) @> '[\"material.qualityDocLink\"]'::jsonb"));

        // Порядок компонентов ключа сохранён ровно как был: 1 наименование, 2 производитель, 3 артикул.
        Assert.Equal("Наименование, Производитель, Артикул", await ScalarAsync(db,
            "SELECT string_agg(k, ', ' ORDER BY n) FROM (SELECT f->>'key' AS k, "
            + "split_part(tag #>> '{}', ':', 2) AS n "
            + "FROM document_types t, jsonb_array_elements(t.\"Schema\"->'fields') f, "
            + "jsonb_array_elements(coalesce(f->'tags', '[]'::jsonb)) tag "
            + "WHERE t.\"Code\" = 'Номенклатура' AND tag #>> '{}' LIKE 'identity:%') x"));

        // Печатный блок строки остался у строки: он печатает количество, которого у справочника нет.
        Assert.Equal(1L, await ScalarAsync(db,
            "SELECT jsonb_array_length(\"Schema\"->'typstRenders')::bigint FROM document_types WHERE \"Code\" = 'Материал'"));

        // И ссылка «Работы» на классификатор — необязательная, с тэгом, по которому её найдёт код.
        Assert.Equal("ref.workType", await ScalarAsync(db,
            "SELECT f->'tags'->>0 FROM document_types t, jsonb_array_elements(t.\"Schema\"->'fields') f "
            + "WHERE t.\"Code\" = 'Работа' AND f->>'key' = 'ВидРаботы'"));
        Assert.Equal(await ScalarAsync(db, "SELECT \"Id\"::text FROM document_types WHERE \"Code\" = 'ВидРаботы'"),
            await ScalarAsync(db,
                "SELECT f->>'typeId' FROM document_types t, jsonb_array_elements(t.\"Schema\"->'fields') f "
                + "WHERE t.\"Code\" = 'Работа' AND f->>'key' = 'ВидРаботы'"));
    }

    /// <summary>
    /// ПОВТОРНЫЙ ПРОГОН и ЧУЖАЯ РАБОТА: миграция не делает ничего дважды и не встраивается в
    /// иерархию, которую строил человек. Второй «Номенклатуры» не появляется, ссылка у «Работы» не
    /// дублируется.
    /// </summary>
    [Fact]
    public async Task Повторный_прогон_второго_справочника_не_заводит()
    {
        await using var db = await CreateDatabaseAsync("bhs_crg_census_lift_twice");
        await MigrateToPreviousAsync(db);
        await SeedLegacyTypesAsync(db);
        await db.Database.MigrateAsync();

        // Прогоняем тело миграции ещё раз — ровно это случилось бы, будь она не идемпотентной.
        // ⚠️ Мимо EF: ExecuteSqlRaw разбирает текст на подстановки и спотыкается о $$ — тело
        // миграции для него «положение параметра, за которым нет цифры».
        await RawAsync(db, MigrationSqlOf("NomenclatureAboveMaterial"));

        Assert.Equal(1L, await ScalarAsync(db,
            "SELECT count(*) FROM document_types WHERE \"Code\" = 'Номенклатура'"));
        Assert.Equal(1L, await ScalarAsync(db,
            "SELECT count(*) FROM document_types t, jsonb_array_elements(t.\"Schema\"->'fields') f "
            + "WHERE t.\"Code\" = 'Работа' AND f->>'key' = 'ВидРаботы'"));
        Assert.Equal("Количество, Цена", await OwnKeysAsync(db, "Материал"));
    }

    /// <summary>
    /// Дорога назад: откат возвращает поля строке и убирает то, что заводила миграция. Полноценный
    /// путь отката — прежний образ плюс резервная копия (решение по G1), но и этот прогон обязан
    /// быть чистым: иначе «туда-обратно» оставляло бы базу с половиной перешивки.
    /// </summary>
    [Fact]
    public async Task Откат_возвращает_поля_строке()
    {
        await using var db = await CreateDatabaseAsync("bhs_crg_census_lift_down");
        await MigrateToPreviousAsync(db);
        await SeedLegacyTypesAsync(db);
        await db.Database.MigrateAsync();

        await MigrateToPreviousAsync(db);

        Assert.Equal(0L, await ScalarAsync(db,
            "SELECT count(*) FROM document_types WHERE \"Code\" IN ('Номенклатура', 'ВидРаботы')"));
        Assert.Equal(
            "Группа, Наименование, Артикул, Производитель, ЕдиницаИзмерения, "
            + "ДокументПодтверждающийКачество, СсылочнаяИнформация, Изображение, Количество, Цена",
            await OwnKeysAsync(db, "Материал"));
        Assert.Null(await ScalarAsync(db, "SELECT \"ParentId\"::text FROM document_types WHERE \"Code\" = 'Материал'"));
        Assert.Equal(0L, await ScalarAsync(db,
            "SELECT count(*) FROM document_types t, jsonb_array_elements(t.\"Schema\"->'fields') f "
            + "WHERE t.\"Code\" = 'Работа' AND f->>'key' = 'ВидРаботы'"));
    }

    /// <summary>
    /// ЧИСТАЯ УСТАНОВКА: «Материала» в ней нет — поднимать нечего, и миграция молчит. Отказ здесь
    /// означал бы, что новая установка не поднимается вовсе (той же ценой обошлась #1046).
    /// </summary>
    [Fact]
    public async Task Пересозданная_база_номенклатуру_не_заводит()
    {
        await using var db = await CreateDatabaseAsync("bhs_crg_census_lift_fresh");
        await db.Database.MigrateAsync();

        Assert.Equal(0L, await ScalarAsync(db,
            "SELECT count(*) FROM document_types WHERE \"Code\" = 'Номенклатура'"));
    }

    /// <summary>Схема ПРЕЖНЕЙ версии: до предпоследней миграции, то есть без проверяемой.</summary>
    private static async Task MigrateToPreviousAsync(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        await db.GetService<IMigrator>().MigrateAsync(all[^2]);
    }

    /// <summary>
    /// Типы, какими они лежат в рабочей базе: единицы измерения, материал-строка и работа-строка.
    /// Возвращает идентификатор единиц — на него ссылаются оба.
    /// </summary>
    private static async Task<Guid> SeedLegacyTypesAsync(AppDbContext db)
    {
        var unit = Guid.NewGuid();
        await InsertTypeAsync(db, unit, "ЕдиницаИзмерения", "Единица измерения", "core", "Open",
            """ {"fields":[{"key":"ЕдиницаИзмерения","title":"Единица измерения","type":"string","tags":["identity"]}]} """);
        await InsertTypeAsync(db, Guid.NewGuid(), "Материал", "Материал", "id", "Open",
            LegacyMaterialSchema.Replace("@unit@", unit.ToString()), group: "Материалы");
        await InsertTypeAsync(db, Guid.NewGuid(), "Работа", "Работа", "id", "Open",
            LegacyWorkSchema.Replace("@unit@", unit.ToString()));
        return unit;
    }

    private static async Task InsertTypeAsync(AppDbContext db, Guid id, string code, string name,
        string module, string level, string schema, string? group = null)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO document_types
                ("Id","Name","Code","Schema","PluginBindings","CreatedAt","UpdatedAt","ParentId",
                 "Kind","IsAbstract","Group","AllowsProxy","Module","ReadChannels","Storage",
                 "Visibility","EditLevel")
            VALUES (@id, @name, @code, @schema::jsonb, '[]'::jsonb, now(), now(), NULL,
                    'Composite', false, @group, false, @module, '', 'SharedObject', 'Shared', @level)
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("code", code);
        cmd.Parameters.AddWithValue("schema", schema);
        cmd.Parameters.AddWithValue("group", (object?)group ?? DBNull.Value);
        cmd.Parameters.AddWithValue("module", module);
        cmd.Parameters.AddWithValue("level", level);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Свои ключи полей типа — в порядке схемы, строкой: так расхождение читается сразу.</summary>
    private static async Task<object?> OwnKeysAsync(AppDbContext db, string code) => await ScalarAsync(db,
        "SELECT string_agg(f->>'key', ', ' ORDER BY ord) FROM document_types t, "
        + "jsonb_array_elements(t.\"Schema\"->'fields') WITH ORDINALITY AS a(f, ord) "
        + "WHERE t.\"Code\" = '" + code + "'");

    /// <summary>
    /// Текст миграции из самой сборки — чтобы повторный прогон проверял НАСТОЯЩИЙ запрос, а не его
    /// копию в тесте. Копия разошлась бы с оригиналом первым же исправлением, и тест остался бы
    /// зелёным, проверяя себя.
    /// </summary>
    private static string MigrationSqlOf(string name)
    {
        var type = typeof(AppDbContext).Assembly.GetTypes()
            .Single(t => typeof(Migration).IsAssignableFrom(t) && t.Name == name);
        var migration = (Migration)Activator.CreateInstance(type)!;
        return string.Join("\n;\n", migration.UpOperations.OfType<SqlOperation>().Select(o => o.Sql));
    }

    private static async Task<AppDbContext> CreateDatabaseAsync(string name)
    {
        await using (var conn = new NpgsqlConnection(AdminConnectionString()))
        {
            await conn.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", conn);
            await drop.ExecuteNonQueryAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", conn);
            await create.ExecuteNonQueryAsync();
        }

        return new AppDbContext(OptionsFor(name));
    }

    /// <summary>
    /// Подключение к служебной базе — для DROP/CREATE. Таймаут поднят: эти команды ждут, пока
    /// сервер освободится, а рядом идёт остальной прогон; тридцати секунд по умолчанию не хватало,
    /// и отказ приходил «таймаутом чтения» — виноватой выглядела база, а не теснота.
    /// </summary>
    private static string AdminConnectionString() =>
        new NpgsqlConnectionStringBuilder(IntegrationTestFixture.TestConnectionString)
        {
            Database = "postgres",
            CommandTimeout = 300,
            Timeout = 60,
        }.ConnectionString;

    /// <summary>Схема с нуля — это десятки миграций подряд, отсюда тот же поднятый таймаут.</summary>
    private static DbContextOptions<AppDbContext> OptionsFor(string database) =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(IntegrationTestFixture.TestConnectionString)
            {
                Database = database,
                CommandTimeout = 300,
                Timeout = 60,
            }.ConnectionString)
            .Options;

    private static async Task ExecAsync(AppDbContext db, string sql) =>
        await db.Database.ExecuteSqlRawAsync(sql);

    /// <summary>Запрос как есть, без разбора подстановок: так его выполняет и сама миграция.</summary>
    private static async Task RawAsync(AppDbContext db, string sql)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(AppDbContext db, string sql)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }
}
