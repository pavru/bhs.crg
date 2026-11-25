using System.Globalization;
using System.Text.Json;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Endpoints;
using BHS.CRG.Modules.Plan.Schedules;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Printing;

namespace BHS.CRG.Modules.Plan.Printing;

internal sealed class ScheduleWorksPrintContext(ScheduleBuilder schedules, IModuleConstructions constructions) : IModulePrintContext
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static readonly ModulePrintRequisites Requisites = new(
    [
        new(["name"], "Название графика", "string"),
        new(["construction"], "Стройка", "string"),
        new(["source"], "Источник работ", "string"),
    ]);

    public async Task<ModulePrintResult> BuildAsync(Guid recordId, IModuleUser user, CancellationToken ct)
    {
        if (!await user.HasAsync(PlanModule.ScheduleRead, ct)) return new ModulePrintResult.Forbidden();

        ScheduleDto schedule;
        try { schedule = await schedules.ReadAsync(recordId, ct); }
        catch (NotFoundException) { return new ModulePrintResult.Missing(); }

        var construction = (await constructions.ListAsync(ct)).FirstOrDefault(c => c.Id == schedule.ConstructionId);
        var sections = construction?.Sections.ToDictionary(section => section.Id, section => section.Name);
        return new ModulePrintResult.Found(JsonSerializer.SerializeToElement(new
        {
            schedule.Name,
            construction = construction?.Name,
            source = schedule.RevisionId.HasValue ? "Редакция сметы" : "Ручной график",
            works = schedule.Works.Select(work => new
            {
                work.Name,
                section = work.SectionId is { } id ? sections?.GetValueOrDefault(id) ?? "Раздел недоступен" : "По стройке в целом",
                work.Start, work.End, work.DurationWorkingDays, work.Volume,
                // Typst читает дробное JSON-число как float и может потерять цифры объёма numeric(18, 6).
                volumeText = work.Volume?.ToString("0.######", CultureInfo.InvariantCulture),
                unit = work.UnitName,
                assignees = work.EmployeeIds.GroupJoin(work.Assignees, id => id, person => person.Id, (_, people) =>
                    people.Select(person => person.Archived ? $"{person.Name} (в архиве)" : person.Name).FirstOrDefault()
                    ?? "Сотрудник недоступен").ToArray(),
                warnings = work.Warnings?.Select(warning => warning.Text).Distinct().ToArray() ?? [],
            }),
        }, Json));
    }
}
