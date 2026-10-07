using System.Text.RegularExpressions;
using BHS.CRG.Tests.Common;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Перепись путей удаления записей ядра (ТЗ CORE-34.1, CORE-34.2; issue #1094): каждый из них обязан
/// спросить, не держат ли запись данные модулей.
///
/// <para>Вопрос этот — отдельный вызов (<c>IRecordHolders</c>), и забыть его легче, чем кажется:
/// индекс ссылок ядра рядом отвечает «никто не ссылается», удаление проходит, тесты зелёные — а строка
/// счёта остаётся со ссылкой в пустоту (issue #1168). Правило «удалять только то, на что не ссылаются»
/// уже обходили с фланга дважды — каскадом уровня (#739) и уборкой сирот (ревью PR #1056).</para>
///
/// <para>Проверка по исходникам и нарочно грубая: файл, который и удаляет что-то, и работает с
/// записями ядра, либо спрашивает держателей (сам или через каскад уровня), либо назван здесь с
/// причиной. Ложное срабатывание стоит одной строки в переписи, пропуск — потерянных ссылок.</para>
///
/// <para>⚠️ <b>Стережёт ФАЙЛ, а не путь удаления</b> (ревью PR #1188). Второй обработчик с удалением в
/// файле, который уже спрашивает, она пропустит. Каждый существующий путь поэтому проверен ещё и
/// поведением — <c>OccupiedRecordDeleteTests</c>; новый путь в старом файле обязан прийти со своим
/// тестом, и напомнить об этом может только ревью.</para>
/// </summary>
public class RecordDeletionInventoryTests
{
    private static readonly string[] Projects =
        SolutionModules.WithCore("BHS.CRG.Api", "BHS.CRG.Application", "BHS.CRG.Infrastructure");

    /// <summary>Файл удаляет — что-нибудь.</summary>
    private static readonly Regex Deletes = new(@"\.Remove\(|RemoveRange\(|ExecuteDeleteAsync", RegexOptions.Compiled);

    /// <summary>Файл работает с записями ядра, на которые модули ссылаются.</summary>
    private static readonly Regex TouchesRecords = new(
        @"IRepository<(DomainObject|Construction|Section|DocumentSet|DocumentType|WorkPlanItem|QualityDocument)>" +
        @"|IDomainObjectRepository" +
        @"|\.(DomainObjects|Constructions|Sections|DocumentSets|DocumentTypes|WorkPlanItems|QualityDocuments)\b",
        RegexOptions.Compiled);

    /// <summary>Файл спрашивает держателей — сам или через каскад уровня, который спрашивает за него.</summary>
    private static readonly Regex Asks = new(@"\bIRecordHolders\b|\bIScopeCascade\b", RegexOptions.Compiled);

    /// <summary>Удаляют, но не записи ядра, — с причиной.</summary>
    private static readonly Dictionary<string, string> DeletesSomethingElse = new()
    {
        ["BHS.CRG.Application/Catalog/Handlers.cs"] =
            "удаляет типы полей, перечисления и записи прежнего каталога; типы документов только читает",
        ["BHS.CRG.Application/Documents/ModuleTypeProjection.cs"] =
            "убирает ключи из JSON схемы типа, записей не удаляет",
        ["BHS.CRG.Application/Documents/PlanHandlers.cs"] =
            "удаляет строки плана комплекта — свои, на них модули не ссылаются",
        ["BHS.CRG.Application/QualityDocs/QualitySetAudit.cs"] =
            "удаляет прежний прогон проверки комплекта",
        ["BHS.CRG.Application/Templates/Handlers.cs"] =
            "удаляет шаблоны и их ассеты; тип документа только читает",
        ["BHS.CRG.Infrastructure/Backup/BackupService.Restore.cs"] =
            "восстановление копии: заменяет файлы библиотеки Typst и спорные псевдонимы сверки, записей не удаляет",
        ["BHS.CRG.Infrastructure/DataFixups/ImageSizeToInstanceFixup.cs"] =
            "убирает ключ из JSON реквизитов",
        ["BHS.CRG.Infrastructure/DataSets/DataSetBindingService.cs"] =
            "удаляет привязки наборов данных",
        ["BHS.CRG.Infrastructure/DataSets/DataSetPdfRecognitionService.Grouping.cs"] =
            "убирает ключ из словаря разбиения PDF",
        ["BHS.CRG.Infrastructure/DataSets/DataSetSourceService.cs"] =
            "удаляет источник набора данных",
        ["BHS.CRG.Infrastructure/Generation/DocumentSetAssemblyService.cs"] =
            "удаляет прежний собранный вывод комплекта",
        ["BHS.CRG.Infrastructure/Subscriptions/SubscriptionService.cs"] =
            "удаляет подписку на рассылку",
    };

    [Fact]
    public void Каждый_путь_удаления_записи_ядра_спрашивает_держателей_в_модулях()
    {
        var silent = Projects.SelectMany(SourceTree.Files)
            .Select(file => (Rel: SourceTree.Relative(file), Text: Code(File.ReadAllText(file))))
            .Where(f => Deletes.IsMatch(f.Text) && TouchesRecords.IsMatch(f.Text))
            .Where(f => !Asks.IsMatch(f.Text) && !DeletesSomethingElse.ContainsKey(f.Rel))
            .Select(f => f.Rel)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(silent.Count == 0,
            "Файлы, которые удаляют и работают с записями ядра, но не спрашивают, держат ли запись " +
            "данные модулей:\n" + string.Join("\n", silent) + "\n\n" +
            "Перед удалением записи, стройки, раздела, комплекта, типа или позиции перечня спросите " +
            "IRecordHolders.FindAsync и откажите через EnsureNone: индекс ссылок ядра таблиц модулей " +
            "не видит, и строка счёта осталась бы со ссылкой в пустоту. Если файл удаляет что-то " +
            "другое — впишите его в DeletesSomethingElse с причиной.");
    }

    /// <summary>
    /// Строка переписи, за которой больше нет удаления, — след переезда. Оставленная, она разрешает
    /// следующему автору этого файла удалять записи молча.
    /// </summary>
    [Fact]
    public void Перепись_не_хранит_умерших_записей()
    {
        var stale = DeletesSomethingElse.Keys
            .Where(rel =>
            {
                var path = Path.Combine(SourceTree.SolutionDir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) return true;
                var text = Code(File.ReadAllText(path));
                return !Deletes.IsMatch(text) || !TouchesRecords.IsMatch(text) || Asks.IsMatch(text);
            })
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(stale.Count == 0,
            "В переписи файлы, которым исключение больше не нужно: " + string.Join(", ", stale) +
            ".\nУберите строки — иначе перепись разрешает то, чего уже не делают.");
    }

    /// <summary>
    /// Принудительное удаление (issue #1187) отправляет ОДИН адрес — тот, что закрыт своим правом.
    /// Команда, созданная вторым местом, дала бы потерю ссылок тому, у кого права нет: порту модуля,
    /// фоновой уборке, соседнему адресу под правом обычной правки.
    /// </summary>
    [Fact]
    public void Принудительное_удаление_отправляет_один_адрес()
    {
        var senders = Projects.SelectMany(SourceTree.Files)
            .Where(file => Code(File.ReadAllText(file)).Contains("new PurgeHeldRecordCommand("))
            .Select(SourceTree.Relative)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["BHS.CRG.Api/Endpoints/Documents/CommonDataEndpoints.cs"], senders);
    }

    /// <summary>
    /// Исходник без комментариев: упоминание порта в комментарии — не вопрос держателям, а удаление,
    /// описанное словами, — не удаление.
    /// </summary>
    private static string Code(string source) =>
        string.Join('\n', source.Split('\n').Where(line => !line.TrimStart().StartsWith("//")));
}
