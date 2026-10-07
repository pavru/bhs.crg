using System.Text.RegularExpressions;
using BHS.CRG.Tests.Common;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Перепись обращений к журналу действий: набор записей трогает ОДНА служба (issue #950, ТЗ CORE-28).
///
/// Почему сторож, а не приватные сеттеры у сущности. Проверки на уровне сущности мало: прямой
/// запрос к базе обходит и сеттеры, и фабрику — <c>db.ActivityRecords.ExecuteUpdate(...)</c>
/// компилируется, работает и меняет свидетельство. Поймать такое можно только перечислением мест,
/// где набор вообще упоминается, и перечислять обязана машина.
///
/// Сторож заодно держит и границу «журнал один на продукт»: как только запись появится вторым
/// путём — из модуля, из фоновой задачи, из восстановления копии, — она пойдёт с другим
/// представлением об авторе и времени, а сводить два журнала потом уже не с чем.
///
/// <para><b>Проекты модулей — под той же переписью</b> (задача M3, issue #1071). Набора
/// <c>ActivityRecords</c> у модуля нет: до контекста ядра он не дотягивается. Зато у него та же база,
/// и строка в таблицу журнала ложится сырым запросом из его собственного контекста — мимо порта
/// <c>IModuleActivityLog</c>, то есть без автора, времени и кода действия из каталога. Поэтому
/// перепись ищет и ИМЯ ТАБЛИЦЫ, а не только имя набора.</para>
///
/// ⚠️ Проверка читает ИСХОДНИКИ: в метаданных сборки «кто обращался к набору» не отражено никак.
/// Приём тот же, что у <see cref="NotificationAudienceInventoryTests" /> и
/// <see cref="Integration.EndpointGateInventoryTests" />.
/// </summary>
public class ActivityLogInventoryTests
{
    private static readonly string[] Projects =
        SolutionModules.WithCore("BHS.CRG.Api", "BHS.CRG.Application", "BHS.CRG.Infrastructure");

    /// <summary>
    /// Кому позволено обращаться к набору записей — и почему. Добавляя строку, вы принимаете
    /// решение: этот код пишет или читает журнал В ОБХОД службы, и так и задумано.
    /// </summary>
    private static readonly Dictionary<string, string> MayTouchTheSet = new()
    {
        ["BHS.CRG.Infrastructure/Activity/ActivityLog.cs"] =
            "сама служба журнала — единственный путь записи и чтения (ТЗ CORE-28)",
        ["BHS.CRG.Infrastructure/Persistence/AppDbContext.cs"] =
            "объявление набора и отказ на правку/удаление записи: там же, где единственная точка сохранения",
        ["BHS.CRG.Infrastructure/Persistence/Configurations/ActivityRecordConfiguration.cs"] =
            "отображение сущности на таблицу: имя таблицы здесь объявлено, запросов нет",
    };

    /// <summary>
    /// Имя набора в контексте базы и имя его таблицы. Упоминание — это и запись, и чтение: и то и
    /// другое мимо службы. Таблица названа ради сырого запроса — единственного пути, которым до
    /// журнала дотянется код без контекста ядра (модуль).
    /// </summary>
    private static readonly Regex Set = new(@"\bActivityRecords\b|\bactivity_log\b", RegexOptions.Compiled);

    [Fact]
    public void К_набору_журнала_обращается_только_его_служба()
    {
        var outsiders = new List<string>();

        // Без миграций: имя таблицы стоит в каждом снимке модели, и запросом это не является.
        foreach (var file in Projects.SelectMany(SourceTree.Files))
        {
            var rel = SourceTree.Relative(file);
            if (MayTouchTheSet.ContainsKey(rel)) continue;

            var text = File.ReadAllText(file);
            foreach (Match m in Set.Matches(text))
                outsiders.Add($"{rel}:{SourceTree.LineOf(text, m.Index)}");
        }

        Assert.True(outsiders.Count == 0,
            "К набору записей журнала обратились мимо службы:\n" + string.Join("\n", outsiders) + "\n\n" +
            "Журнал пишется и читается через IActivityLog (модуль — через порт IModuleActivityLog): только там у записи есть автор, время и " +
            "код действия из каталога, и только там правка записи невозможна. Если обращение " +
            "всё-таки нужно — впишите файл в MayTouchTheSet с причиной.");
    }

    /// <summary>
    /// Запись в переписи, за которой больше нет обращений, — след переезда. Оставленная, она тихо
    /// разрешает следующему автору этого файла писать в журнал напрямую.
    /// </summary>
    [Fact]
    public void Перепись_не_хранит_умерших_записей()
    {
        var stale = MayTouchTheSet.Keys
            .Where(rel =>
            {
                var path = Path.Combine(SourceTree.SolutionDir, rel.Replace('/', Path.DirectorySeparatorChar));
                return !File.Exists(path) || !Set.IsMatch(File.ReadAllText(path));
            })
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(stale.Count == 0,
            "В переписи файлы, где обращений к журналу больше нет: " + string.Join(", ", stale) +
            ".\nУберите строки — иначе перепись разрешает то, чего уже не делают.");
    }

    /// <summary>
    /// Записи журнала неизменяемы и в коде: ни одного открытого сеттера у свойств сущности.
    ///
    /// Проверка отдельная от отказа в <c>SaveChanges</c> нарочно. Отказ ловит попытку сохранить
    /// правку — то есть уже написанный код; открытый сеттер приглашает её написать, и заметен он
    /// только тому, кто пойдёт читать сущность.
    /// </summary>
    [Fact]
    public void У_записи_журнала_нет_открытых_сеттеров()
    {
        var open = typeof(BHS.CRG.Domain.Activity.ActivityRecord)
            .GetProperties()
            .Where(p => p.SetMethod is { IsPublic: true })
            .Select(p => p.Name)
            .ToList();

        Assert.True(open.Count == 0,
            "У записи журнала появились открытые сеттеры: " + string.Join(", ", open) +
            ". Запись только создаётся (ActivityRecord.Create) и больше не меняется (ТЗ CORE-28).");
    }
}
