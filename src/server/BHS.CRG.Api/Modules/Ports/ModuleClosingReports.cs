using BHS.CRG.Application.Periods;
using BHS.CRG.Domain.Periods;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Ports;
using PeriodContour = BHS.CRG.Domain.Periods.PeriodContour;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Собирает разделы модулей для диалога закрытия периода (ТЗ CORE-35; задача E1b, issue #1099).
///
/// <para>Живёт в корне композиции по направлению ссылок — как <see cref="ModuleWorkRunner" />: служба
/// закрытия в инфраструктуре о контрактах модулей не знает, а кто из модулей что скажет, известно
/// здесь.</para>
///
/// <para>Название раздела — из реестра модулей, а не из текста модуля: человек видит модуль под тем же
/// именем, что в настройке и в меню. Порядок разделов — порядок модулей в реестре, а не порядок
/// регистрации служб: от него зависит отпечаток перечня, и он обязан быть одним на предпросмотр и на
/// закрытие.</para>
/// </summary>
public sealed class ModuleClosingReports(IEnumerable<IModuleClosingReport> reports, ModuleRegistry modules)
    : IClosingReports
{
    public async Task<ClosingReport> CollectAsync(
        PeriodContour contour, DateOnly? from, DateOnly through, IReadOnlyDictionary<Guid, DateOnly> closedAhead,
        CancellationToken ct = default)
    {
        var byModule = reports.ToLookup(r => r.Module, StringComparer.Ordinal);
        foreach (var group in byModule)
        {
            if (!modules.IsEnabled(group.Key))
                throw new InvalidOperationException(
                    $"Раздел диалога закрытия объявлен за модуль «{group.Key}», а такого модуля в составе нет. " +
                    $"Включены: {string.Join(", ", modules.Codes)}.");
            if (group.Count() > 1)
                throw new InvalidOperationException(
                    $"Раздел диалога закрытия за модуль «{group.Key}» объявили несколько служб: " +
                    string.Join(", ", group.Select(r => r.GetType().FullName)) + ". Раздел у модуля один.");
        }

        var scope = new ModuleClosingScope(contour.ConstructionId, from, through, closedAhead);
        var sections = new List<ClosingSection>();
        foreach (var code in modules.Codes)
        {
            if (byModule[code].SingleOrDefault() is not { } report) continue;

            // Отказ модуля не ловим: он обязан стать отказом закрытия. Раздел, пропущенный из-за ошибки,
            // выглядел бы как «незавершённого нет».
            var section = await report.ReportAsync(scope, ct);
            // Ссылка строки ляжет в неизменяемую запись и станет адресом в экране ядра — только путь
            // внутри приложения. Чужой адрес отсюда вёл бы человека наружу под видом «посмотреть счета».
            if (section.Unfinished.Concat(section.Frozen).FirstOrDefault(l => l.Link is { } link && !IsLocal(link)) is { } foreign)
                throw new InvalidOperationException(
                    $"Модуль «{code}» дал строке диалога закрытия «{foreign.Key}» ссылку «{foreign.Link}». " +
                    "Ссылка строки — путь внутри приложения, от корня: «/tables/…».");
            sections.Add(new(code, modules.Find(code)!.Title, section.DateRule,
                [.. section.Unfinished.Select(Line)], [.. section.Frozen.Select(Line)]));
        }

        return new(sections);
    }

    private static bool IsLocal(string link) =>
        link.StartsWith('/') && !link.StartsWith("//") && !link.Contains('\\');

    private static ClosingLine Line(ModuleClosingLine line) => new(
        line.Key, line.Text, line.Count, new(line.Unit.One, line.Unit.Few, line.Unit.Many),
        line.Amount, line.AmountPermission, line.Note, line.Link);
}
