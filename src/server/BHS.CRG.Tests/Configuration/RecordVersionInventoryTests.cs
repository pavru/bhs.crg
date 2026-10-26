using BHS.CRG.Tests.Common;
using static BHS.CRG.Tests.Configuration.RecordVersionCensus;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Перепись обращений к истории версий записей модулей (ТЗ CORE-36, change <c>plan-12-record-versions</c>):
/// наследников <c>ModuleRecordVersion</c> во всех модулях пишет и читает только названный файл.
///
/// <para><b>От чего это.</b> Действующая версия лежит в основной таблице, прежние — в таблице истории, и
/// запросы сумм историю не читают. Одно чтение истории из итогов молча удвоило бы цифры; одна запись мимо
/// места исправления — версию без блокировки строки записи и без журнала. Правку и удаление через
/// отслеживание отвергает сам контекст (<c>IAppendOnly</c>); эта перепись закрывает остальное: второе
/// место записи, чтение из итогов, <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> и таблицу по имени в
/// сыром SQL.</para>
///
/// <para>Образец — <see cref="InvoiceBookInventoryTests" />: словарь «кому позволено и почему». Здесь он
/// один на все модули и заводится на каждого наследника: наследник без словаря — красный, а не «никому
/// нельзя», чтобы решение о доступе принималось вслух, вместе с таблицей.</para>
/// </summary>
public class RecordVersionInventoryTests
{
    /// <summary>
    /// Наследник → файлы, которым позволено обращаться к его набору, и почему. Файл объявления типа
    /// разрешён сам (тип и его отображение на таблицу). Добавляя строку, вы принимаете решение: этот файл
    /// пишет историю или читает её как историю — не для сумм.
    /// </summary>
    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> MayTouch = new()
    {
        ["WaybillVersion"] = new Dictionary<string, string>
        {
            ["BHS.CRG.Modules.Costs/Data/CostsDbContext.cs"] = "объявление набора",
            ["BHS.CRG.Modules.Costs/Endpoints/WaybillCorrection.cs"] =
                "единственное место записи (исправление под замком периода, в одной транзакции с правкой) и " +
                "чтения истории: адрес версий и пометка списка «исправлена после закрытия» — день и причина, " +
                "без сумм (issue #1340)",
        },
    };

    /// <summary>
    /// Файлы итогов, которые не узнать по имени (<c>*Totals.cs</c> и <c>*ClosingReport.cs</c> узнаются сами),
    /// и почему они итоги. Из них к истории не обращаются, даже названные в <see cref="MayTouch" />.
    /// <c>*Views.cs</c> по имени не узнаётся: так зовутся и сборщики представлений.
    /// </summary>
    private static readonly Dictionary<string, string> TotalsFiles = new()
    {
        ["BHS.CRG.Modules.Costs/Data/CostViews.cs"] = "взгляды затрат стройки — суммы по счетам и разноске",
        ["BHS.CRG.Modules.Costs/Data/IssuedMaterials.cs"] =
            "«Материалы на объекте» — свод отпущенного по стройке: только действующие строки накладных",
        ["BHS.CRG.Modules.Costs/Data/WaybillClosingLines.cs"] =
            "числа накладных в диалоге закрытия — они ложатся в неизменяемую запись о закрытии",
    };

    /// <summary>Дописываемые типы с формой версии мимо базового типа — и почему.</summary>
    private static readonly Dictionary<string, string> ShapeExceptions = new()
    {
        ["InvoiceVersion"] =
            "версии счёта построены раньше базового типа (E2, #1100) и живут в базе заказчика: живые данные не " +
            "переносятся ради имени. Их место записи стережёт InvoiceBookInventoryTests",
    };

    private static Rules Solution => new(MayTouch, TotalsFiles, ShapeExceptions);

    // Ядро — тоже: сырой SQL по таблице модуля пишется и там (резервные копии, обслуживание базы).
    private static IReadOnlyList<Source> SolutionSources() =>
        [.. SolutionModules.WithCore("BHS.CRG.Api", "BHS.CRG.Application", "BHS.CRG.Infrastructure")
            .SelectMany(SourceTree.Files)
            .Select(full => Source.Read(full, SourceTree.Relative(full)))];

    [Fact]
    public void К_истории_версий_обращаются_только_названные_файлы()
    {
        var found = Scan(SolutionSources(), Solution);

        Assert.True(found.Count == 0,
            "Обращения к истории версий записей модулей мимо правил:\n" + string.Join("\n", found) + "\n\n" +
            $"«{NoDictionary}» — наследник ModuleRecordVersion без словаря: впишите в MayTouch, какие файлы его " +
            "пишут и читают историю, и почему. " +
            $"«{NoMap}» — таблица наследника не названа строкой в ModuleRecordVersion.Map<Версия, Запись>(builder, " +
            "\"таблица\"): без него нет индекса номера и связи Restrict, а сторожу не по чему искать сырой SQL. " +
            $"«{Outsider}» — обращение из файла вне словаря. " +
            $"«{Totals}» — итоги читают историю: суммы считаются только по действующим версиям (основная таблица). " +
            $"«{Bypass}» и «{Table}» — ExecuteUpdate/ExecuteDelete, сырой SQL или таблица по имени: история " +
            "только дописывается, и мимо отслеживания её не защищает ничто. " +
            $"«{Shape}» — дописываемый тип с номером и содержимым мимо базового типа: наследуйте " +
            "ModuleRecordVersion или впишите тип в ShapeExceptions с причиной.");
    }

    [Fact]
    public void Перепись_не_хранит_умерших_записей()
    {
        var files = SolutionSources();
        var heirs = Heirs(files).ToDictionary(h => h.Type);
        var stale = new List<string>();

        foreach (var (type, mayTouch) in MayTouch)
        {
            if (!heirs.TryGetValue(type, out var heir))
            {
                stale.Add($"{type}: наследника больше нет");
                continue;
            }

            stale.AddRange(mayTouch.Keys
                .Where(rel => files.FirstOrDefault(f => f.Rel == rel) is not { } f || !heir.Access.IsMatch(f.Text))
                .Select(rel => $"{type}: {rel} — обращения больше нет"));
        }

        stale.AddRange(TotalsFiles.Keys.Where(rel => files.All(f => f.Rel != rel)).Select(rel => $"итоги: {rel} — файла нет"));

        var shapes = ShapeClasses(files).ToHashSet();
        stale.AddRange(ShapeExceptions.Keys.Where(t => !shapes.Contains(t)).Select(t => $"исключение формы: {t} — типа нет"));

        Assert.True(stale.Count == 0, "В переписи истории версий умершие записи:\n" + string.Join("\n", stale));
    }

    /// <summary>
    /// Каждый запрет красный на своём образце, и ничего сверх (<c>TestData/RecordVersionInventory</c>).
    /// Сравнение точное: снятый запрет убирает свою строку, ослабленный отбор добавляет чужую — оба красные.
    /// </summary>
    [Fact]
    public void Каждый_запрет_красный_на_своём_образце()
    {
        var dir = Path.Combine(SourceTree.SolutionDir, "BHS.CRG.Tests", "TestData", "RecordVersionInventory");
        var samples = Directory.EnumerateFiles(dir, "*.cs.txt", SearchOption.AllDirectories)
            .Select(full => Source.Read(full, Path.GetRelativePath(dir, full).Replace('\\', '/')[..^".txt".Length]))
            .ToList();

        var rules = new Rules(
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["SampleVersion"] = new Dictionary<string, string>
                {
                    ["Sample/Data/SampleDbContext.cs"] = "объявление набора",
                    ["Sample/Endpoints/SampleCorrection.cs"] = "место записи",
                    ["Sample/Data/SampleIssued.cs"] = "назван, но он — итоги: разрешение не действует",
                    ["Sample/Data/SampleEstimateViews.cs"] = "сборщик представлений читает историю как историю",
                },
                ["SampleMapped"] = new Dictionary<string, string> { ["Sample/Data/SampleDbContext.cs"] = "объявление и отображение" },
                ["SampleUnmapped"] = new Dictionary<string, string>(),
            },
            new Dictionary<string, string> { ["Sample/Data/SampleIssued.cs"] = "перечень отпущенного — итоги" },
            new Dictionary<string, string>());

        var got = Scan(samples, rules).Select(v => (v.Code, v.Type, v.Rel)).Order().ToList();
        (string, string, string)[] expected =
        [
            (Bypass, "SampleVersion", "Sample/Endpoints/SampleCorrection.cs"),
            (NoDictionary, "SampleStray", "Sample/Data/SampleStray.cs"),
            (NoMap, "SampleUnmapped", "Sample/Data/SampleUnmapped.cs"),
            (Outsider, "SampleVersion", "Sample/Data/SampleReport.cs"),
            (Shape, "SampleShape", "Sample/Data/SampleShape.cs"),
            (Table, "SampleVersion", "Sample/Data/SampleRaw.cs"),
            (Totals, "SampleVersion", "Sample/Data/SampleIssued.cs"),
            (Totals, "SampleVersion", "Sample/Data/SampleTotals.cs"),
        ];

        Assert.Equal(expected.Order().ToList(), got);
    }
}
