using BHS.CRG.Application.Objects;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BHS.CRG.Infrastructure.Persistence;

/// <inheritdoc cref="IReferenceIndex" />
public class ReferenceIndex(AppDbContext db) : IReferenceIndex
{
    public Task<IReadOnlyList<Guid>> ObjectsMentioningAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
        => QueryAsync(Sql("domain_objects", "Data"), ids, ct);

    public Task<IReadOnlyList<Guid>> QualityDocumentsMentioningAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
        => QueryAsync(Sql("quality_documents", "Requisites"), ids, ct);

    /// <summary>
    /// Строки, в JSONB которых есть строковый узел, равный одному из идентификаторов.
    ///
    /// <para><c>jsonb_path_query('$.**')</c> обходит РАЗОБРАННЫЙ jsonb, а не текст, — ссылка лежит
    /// на произвольной глубине (внутри массивов строк таблицы, внутри составных значений), и по
    /// верхнему уровню её не найти. Грубого предварительного отбора по <c>::text</c>, как в
    /// сканере блобов, здесь сознательно нет: там искали строку неизвестной формы во ВСЕХ JSONB
    /// схемы, и отбор экономил разбор чужих таблиц; здесь колонок две и известных, а
    /// материализация той же самой строки в текст стоила бы ровно того, что призвана сэкономить.</para>
    ///
    /// <para><b>Сравнение по нормализованной форме.</b> <c>Guid.TryParse</c> в C# принимает все свои
    /// формы записи — <c>D</c> (с дефисами), <c>N</c>, в фигурных и круглых скобках, <c>X</c> с
    /// префиксами <c>0x</c>, — и вдобавок терпит пробелы по краям; регистр ему безразличен. Прямое
    /// сравнение строк не приняло бы ничего, кроме точного совпадения. Сузь мы у́же, чем понимает
    /// C#, — держатель, чей идентификатор записан иначе, не попал бы в отбор, guard пропустил бы
    /// настоящую ссылку, и удаление оборвало бы её, выглядя при этом как успех: ни ошибки, ни
    /// предупреждения. Поэтому обе стороны приводятся к 32 знакам в нижнем регистре: снимаются
    /// скобки, дефисы, запятые и пробелы, затем префиксы <c>0x</c>. Посторонняя строка после такой
    /// нормализации совпасть с идентификатором практически не может, а совпади — лишний кандидат
    /// стоит одного разбора JSON, тогда как пропущенный стоит висячей ссылки.</para>
    ///
    /// <para><b><c>IN (SELECT unnest(…))</c>, а не <c>= ANY(@ids)</c>.</b> Разница не стилистическая:
    /// <c>= ANY</c> по массиву — линейный перебор, и выполняется он на КАЖДЫЙ строковый узел каждой
    /// строки. Идентификаторов бывает много: каскад удаления стройки передаёт сюда всё её поддерево,
    /// а уборка сирот — всех сирот базы. На 2000 строках по 32 КБ и 3000 идентификаторах эта форма
    /// не уложилась в десять минут; <c>IN</c> с распаковкой в подзапрос даёт полусоединение по хешу
    /// и те же данные проходит за 2,7 с. Прежний код, читавший таблицы целиком, искал по
    /// <c>HashSet</c> — то есть без этой правки «ускорение» на больших наборах было бы замедлением.</para>
    /// </summary>
    private static string Sql(string table, string column) =>
        "SELECT x.\"Id\" FROM " + table + " x WHERE EXISTS ("
        + " SELECT 1 FROM jsonb_path_query(x.\"" + column + "\", '$.**') v"
        + " WHERE jsonb_typeof(v) = 'string'"
        + "   AND replace(lower(translate(v #>> '{}', '{}()-, \t\r\n', '')), '0x', '')"
        + "       IN (SELECT unnest(@ids)))";

    private async Task<IReadOnlyList<Guid>> QueryAsync(
        string sql, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere) await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            // Умолчание — 30 с, и оно здесь опаснее, чем кажется: проверка стоит на ИНТЕРАКТИВНОМ
            // пути, и её обрыв даёт не «нельзя удалить, ссылаются такие-то» и не «можно», а 500-ю.
            // Тот же запас, что у соседнего сканера по тем же JSONB (LiveBlobPathScan).
            cmd.CommandTimeout = 600;
            // «N» — те же 32 знака без дефисов и в нижнем регистре, что даёт нормализация в SQL.
            cmd.Parameters.Add(new NpgsqlParameter("ids", ids.Select(i => i.ToString("N")).ToArray()));

            var found = new List<Guid>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) found.Add(reader.GetGuid(0));
            return found;
        }
        finally
        {
            if (openedHere) await db.Database.CloseConnectionAsync();
        }
    }
}
