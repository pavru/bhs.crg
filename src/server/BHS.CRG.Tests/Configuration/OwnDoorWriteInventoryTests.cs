using System.Text.RegularExpressions;
using BHS.CRG.Tests.Common;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Перепись мест, которые пишут записи общих данных командами: каждое либо само дверь справочника со
/// своим правом (<c>OwnDoorTypes</c>, сейчас — виды работ), либо отвергает его записи
/// (<c>OwnDoorTypes.RefuseCommonPathAsync</c> выше команды), либо названо исключением с причиной,
/// почему записи такого справочника до него не доходят (WORK-59, риск из design.md plan-03).
///
/// <para>Правило двери держится на том, что у вида работы нет второго входа: путь, который заводит
/// или правит запись любого типа под другим правом, открывал бы классификатор без
/// <c>core.worktypes.edit</c> — и выглядел бы исправным. По исходникам и грубо, как
/// <see cref="ClosedTypeCreationInventoryTests" /> (заведение мимо команд стережёт она): что отказ
/// действительно приходит, проверяет <c>WorkTypeEndpointsTests</c>; здесь — что о новом месте
/// записи нельзя забыть.</para>
/// </summary>
public class OwnDoorWriteInventoryTests
{
    private static readonly string[] Projects =
        SolutionModules.WithCore("BHS.CRG.Application", "BHS.CRG.Api", "BHS.CRG.Infrastructure");

    private static readonly Regex Write = new(
        @"new (CreateCommonDataEntryCommand|UpdateCommonDataEntryCommand|DeleteCommonDataEntryCommand|" +
        @"SetRecordArchiveCommand|PurgeHeldRecordCommand|PurgeHeldRecordsCommand|PurgeHeldScopeCommand)\(",
        RegexOptions.Compiled);

    /// <summary>Сколько строк выше команды ищется отказ.</summary>
    private const int Lookback = 15;

    private const string Door = "дверь";
    private const string Refuses = "отвергает";

    private const string AdminPurge =
        "принудительное удаление — путь администратора под core.catalog.purge, а не правка; убрать запись, " +
        "которую держат только данные снятого модуля, нужно и у вида работы (см. OwnDoorTypes)";

    /// <summary>Ключ — «файл|строка кода»; значение — <see cref="Door" />, <see cref="Refuses" /> или причина.</summary>
    private static readonly Dictionary<string, string> Sites = new(StringComparer.Ordinal)
    {
        ["BHS.CRG.Api/Endpoints/Core/WorkTypeEndpoints.cs|return Results.Ok(CommonDataEntryDto.From(await m.Send(new CreateCommonDataEntryCommand("] = Door,
        ["BHS.CRG.Api/Endpoints/Core/WorkTypeEndpoints.cs|return Results.Ok(CommonDataEntryDto.From(await m.Send(new UpdateCommonDataEntryCommand("] = Door,
        ["BHS.CRG.Api/Endpoints/Core/WorkTypeEndpoints.cs|try { await m.Send(new DeleteCommonDataEntryCommand(id), ct); return Results.NoContent(); }"] = Door,
        ["BHS.CRG.Api/Endpoints/Core/WorkTypeEndpoints.cs|var done = await m.Send(new SetRecordArchiveCommand(id, archived), ct);"] = Door,

        ["BHS.CRG.Api/Endpoints/Documents/CommonDataEndpoints.cs|return Results.Ok(CommonDataEntryDto.From(await m.Send(new CreateCommonDataEntryCommand("] = Refuses,
        ["BHS.CRG.Api/Endpoints/Documents/CommonDataEndpoints.cs|return Results.Ok(CommonDataEntryDto.From(await m.Send(new UpdateCommonDataEntryCommand("] = Refuses,
        ["BHS.CRG.Api/Endpoints/Documents/CommonDataEndpoints.cs|try { await m.Send(new DeleteCommonDataEntryCommand(id)); return Results.NoContent(); }"] = Refuses,
        ["BHS.CRG.Api/Endpoints/Documents/CommonDataEndpoints.cs|try { return Results.Ok(await m.Send(new SetRecordArchiveCommand(id, archived))); }"] =
            "общий помощник архива: отправку в архив отвергает вызов у адреса /archive, а возврат из архива " +
            "свободен нарочно — снять признак всегда благо (OwnDoorTypes.RefuseCommonPathAsync)",
        ["BHS.CRG.Api/Endpoints/Documents/CommonDataEndpoints.cs|try { return Results.Ok(await m.Send(new PurgeHeldRecordCommand(id, req.References))); }"] = AdminPurge,
        ["BHS.CRG.Api/Endpoints/Documents/RecordBatchPurgeEndpoints.cs|return Results.Ok(await m.Send(new PurgeHeldRecordsCommand("] = AdminPurge,
        ["BHS.CRG.Api/Endpoints/Core/ScopePurge.cs|try { return Results.Ok(await m.Send(new PurgeHeldScopeCommand(level, id, req.References), ct)); }"] =
            "удаление уровня целиком (стройки, раздела) — путь администратора; виды работ живут на уровне " +
            "«Система», а не стройки",

        ["BHS.CRG.Api/Endpoints/Core/EmployeeEndpoints.cs|return Results.Ok(CommonDataEntryDto.From(await m.Send(new CreateCommonDataEntryCommand("] =
            "тип выбирает сама дверь — «Сотрудник» ядра; назвать вид работы ей нечем",
        ["BHS.CRG.Api/Endpoints/Core/EmployeeEndpoints.cs|return Results.Ok(CommonDataEntryDto.From(await m.Send(new UpdateCommonDataEntryCommand("] =
            "дверь видит только записи типа «Сотрудник»: вид работы для неё не существует (404)",
        ["BHS.CRG.Api/Endpoints/Core/EmployeeEndpoints.cs|try { await m.Send(new DeleteCommonDataEntryCommand(id)); return Results.NoContent(); }"] =
            "то же: запись не сотрудника для двери не существует",
        ["BHS.CRG.Api/Modules/Ports/ModuleOwnCatalogPort.cs|var entry = await mediator.Send(new CreateCommonDataEntryCommand("] =
            "только собственные справочники модуля (OwnTypeAsync): владелец вида работы — ядро",
        ["BHS.CRG.Api/Modules/Ports/ModuleOwnCatalogPort.cs|await mediator.Send(new DeleteCommonDataEntryCommand(id), ct);"] =
            "то же: запись находится только среди справочников модуля (EntryAsync)",
        ["BHS.CRG.Infrastructure/Documents/CatalogIntakeService.cs|var created = await mediator.Send(new CreateCommonDataEntryCommand("] =
            "заводит в справочник, названный ролью из IntakeTypes модуля (поставщик, покупатель); ни одна " +
            "роль на классификатор видов работ не указывает — объявите такую, и строке здесь место в Refuses",
        ["BHS.CRG.Infrastructure/Documents/NomenclatureIntakeService.cs|var created = await mediator.Send(new CreateCommonDataEntryCommand("] =
            "заводит только в семью номенклатуры (роль role.nomenclature): вид работы — не материал",
    };

    [Fact]
    public void Каждое_место_записи_названо_и_рассуждено()
    {
        var found = Find();

        var undeclared = found.Keys.Where(k => !Sites.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        Assert.True(undeclared.Count == 0,
            "Появилось место, которое пишет записи общих данных, а о справочниках со своей дверью (виды " +
            "работ, WORK-59) оно не знает:\n" + string.Join("\n", undeclared) +
            "\n\nПозовите OwnDoorTypes.RefuseCommonPathAsync выше команды и впишите строку в Sites как " +
            "Refuses — либо впишите причину, почему запись такого справочника сюда не доходит.");

        var stale = Sites.Keys.Where(k => !found.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0,
            "В переписи места, которых в коде больше нет:\n" + string.Join("\n", stale) +
            "\nУберите строки — иначе перепись описывает несуществующее.");
    }

    [Fact]
    public void Названное_отвергающим_действительно_отвергает()
    {
        var found = Find();
        var wrong = new List<string>();
        foreach (var (key, verdict) in Sites)
        {
            if (!found.TryGetValue(key, out var refusesNearby)) continue; // о пропаже говорит соседний тест
            if (verdict == Refuses && !refusesNearby)
                wrong.Add($"{key}\n    назван отвергающим, но OwnDoorTypes.RefuseCommonPathAsync выше нет");
            if (verdict == Door && !key.StartsWith("BHS.CRG.Api/Endpoints/Core/WorkTypeEndpoints.cs|", StringComparison.Ordinal))
                wrong.Add($"{key}\n    назван дверью, а дверь видов работ — WorkTypeEndpoints");
        }
        Assert.True(wrong.Count == 0, "Перепись разошлась с кодом:\n" + string.Join("\n", wrong));
    }

    /// <summary>Место записи → стоит ли выше отказ общего адреса.</summary>
    private static Dictionary<string, bool> Find()
    {
        var found = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var file in Projects.SelectMany(SourceTree.Files))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("//") || !Write.IsMatch(lines[i])) continue;
                var refuses = false;
                for (var back = Math.Max(0, i - Lookback); back < i; back++)
                    if (!lines[back].TrimStart().StartsWith("//") && lines[back].Contains("OwnDoorTypes.RefuseCommonPathAsync(")) refuses = true;
                found[$"{SourceTree.Relative(file)}|{lines[i].Trim()}"] = refuses;
            }
        }
        return found;
    }
}
