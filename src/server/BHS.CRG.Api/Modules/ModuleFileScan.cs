using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Infrastructure.Storage;
using BHS.CRG.Modules.Data;

namespace BHS.CRG.Api.Modules;

/// <summary>
/// Что обход строк модуля нашёл про файлы (issue #1261).
/// </summary>
/// <param name="Groups">Файлы, которые едут в копию: слова объявления (или адрес необъявленной
/// колонки) → пути.</param>
/// <param name="Undeclared">Адреса колонок, где путь найден, а модуль о колонке промолчал. Файлы из
/// них в <paramref name="Groups" /> ЕСТЬ — под адресом вместо слов.</param>
/// <param name="Embedded">Адрес колонки → сколько в ней ссылок на файл, записанных ВНУТРИ текста:
/// ключ пути найден, точного пути нет. Эти файлы в копию не попадают.</param>
public sealed record ModuleFilesFound(
    IReadOnlyDictionary<string, IReadOnlySet<string>> Groups,
    IReadOnlyList<string> Undeclared,
    IReadOnlyDictionary<string, int> Embedded)
{
    /// <summary>Все пути разом, без повторов и в устойчивом порядке — так они и ложатся в копию.</summary>
    public string[] Paths => [.. Groups.Values.SelectMany(g => g).Distinct(StringComparer.Ordinal)
        .OrderBy(p => p, StringComparer.Ordinal)];
}

/// <summary>
/// Файлы модуля — по его строкам в резервной копии (ТЗ CORE-34, CORE-29, issue #1261).
///
/// <para><b>Находит обход, а не объявление.</b> Строки модуля копия уже сняла (<c>to_jsonb</c> по
/// каждой таблице), и путь файла в них — строка известной формы
/// (<see cref="BlobPathShape.Pattern" />). Берётся каждая такая строка в каждой колонке, на любой
/// глубине JSON. Объявление модуля (<see cref="ModuleFile" />) только называет найденное словами и
/// освобождает колонку с производным файлом. Новая колонка со сканом — накладной, выгрузки 1С —
/// попадает в копию без правки здесь и без объявления; объявление потребует инвентарный тест.</para>
///
/// <para>Тот же приём у уборки осиротевших файлов (<c>LiveBlobPathScan</c>) и у удержания записей
/// (<see cref="ModuleReference" />): один вопрос — «что держит модуль» — решён одним способом, и
/// разойтись копии с уборкой нечем: обе опознают путь по одной форме.</para>
///
/// <para><b>⚠️ Чего обход не берёт.</b> Копии нужен ТОЧНЫЙ путь, а не ключ, поэтому путь, записанный
/// внутри текста (JSON строкой, после адреса сервера), найден будет, а взят — нет: где в тексте
/// кончается имя файла, не понять. О таких копия говорит предупреждением с адресом колонки. Сегодня
/// таких держателей нет; появится — путь по ключу отдаст реестр блобов.</para>
///
/// <para>Что считать точным путём, решает <see cref="BlobPathShape.Inspect" /> — одно правило на ядро
/// и модули (issue #1422): копия ядра обходит свой манифест тем же правилом.</para>
///
/// <para>Чистая функция нарочно: без базы и без хранилища, чтобы правило проверялось прямо.</para>
/// </summary>
public static class ModuleFileScan
{
    public static ModuleFilesFound Scan(
        IEnumerable<BackupModuleTable> tables, IReadOnlyList<ModuleFile> declared)
    {
        var byColumn = declared.ToDictionary(f => (f.Table, f.Column));
        var groups = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var undeclared = new SortedSet<string>(StringComparer.Ordinal);
        var embedded = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (var table in tables)
        foreach (var row in table.Rows)
        {
            if (row.ValueKind != JsonValueKind.Object) continue;

            foreach (var column in row.EnumerateObject())
            {
                byColumn.TryGetValue((table.Table, column.Name), out var declaration);
                if (declaration is { Carried: false }) continue;

                var address = $"{table.Table}.{column.Name}";
                BlobPathShape.VisitStrings(column.Value, text =>
                {
                    if (BlobPathShape.Inspect(text) is not { } hit) return;

                    if (hit.Exact)
                    {
                        if (declaration is null) undeclared.Add(address);
                        var what = declaration?.What ?? address;
                        if (!groups.TryGetValue(what, out var group))
                            groups[what] = group = new HashSet<string>(StringComparer.Ordinal);
                        group.Add(text);
                    }
                    else
                    {
                        embedded[address] = embedded.GetValueOrDefault(address) + hit.Keys.Count;
                    }
                });
            }
        }

        return new ModuleFilesFound(
            groups.ToDictionary(g => g.Key, g => (IReadOnlySet<string>)g.Value, StringComparer.Ordinal),
            [.. undeclared], embedded);
    }
}
