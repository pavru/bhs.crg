namespace BHS.CRG.Domain.Templates;

/// <summary>
/// Приведение версий шаблона к правилам (id-template-version-integrity, D7–D8): у шаблона одна
/// действующая версия, и номер версии внутри него не повторяется. Шаблон — все строки с одним типом
/// документа и одним названием, как их группирует страница шаблонов.
/// <para>
/// Правило одно на два места: восстановление копии вызывает эту функцию, а миграция
/// <c>TemplateVersionIntegrity</c> повторяет её в SQL — для накопленных в базе дублей. Совпадение
/// сторожит тест: одни и те же дубли проходят через оба, итог сравнивается. Меняя правило здесь,
/// меняйте и SQL — иначе починка в базе и при восстановлении разойдутся.
/// </para>
/// Порядок шагов — как в D8: сначала гасятся лишние действующие (по прежним номерам), затем
/// перенумеровываются повторы.
/// </summary>
public static class TemplateVersionRepair
{
    public enum ChangeKind { Retired, Renumbered }

    /// <summary>Что сделано с версией — для отчёта восстановления.</summary>
    public sealed record Change(Template Template, ChangeKind Kind, int FromVersion, int ToVersion)
    {
        public string Describe() => Kind == ChangeKind.Retired
            ? $"Шаблон «{Template.Name}»: лишняя действующая версия v{FromVersion} погашена."
            : $"Шаблон «{Template.Name}»: повторная v{FromVersion} (создана {Template.CreatedAt:yyyy-MM-dd HH:mm}) " +
              $"перенумерована в v{ToVersion}.";
    }

    /// <summary>
    /// Приводит переданные версии к правилам на месте и возвращает каждое изменение. Версии разных
    /// шаблонов можно передавать вместе — они разбираются по группам.
    /// </summary>
    public static IReadOnlyList<Change> Apply(IEnumerable<Template> templates)
    {
        var changes = new List<Change>();
        foreach (var group in templates.GroupBy(t => (t.DocumentTypeId, t.Name)))
        {
            var versions = group.ToList();
            RetireExtraActive(versions, changes);
            RenumberRepeats(versions, changes);
        }
        return changes;
    }

    /// <summary>
    /// Каждая погашенная версия шаблона, у которого есть действующая, — к этой действующей. По этой
    /// карте закрепления документов пересаживаются на шаблон, а не остаются на погашенной строке
    /// (вариант А #1923).
    /// </summary>
    public static IReadOnlyDictionary<Guid, Guid> ActiveByRetired(
        IEnumerable<(Guid Id, Guid DocumentTypeId, string Name, bool IsActive)> templates)
    {
        var map = new Dictionary<Guid, Guid>();
        foreach (var group in templates.GroupBy(t => (t.DocumentTypeId, t.Name)))
        {
            var active = group.Where(t => t.IsActive).Select(t => t.Id).ToList();
            if (active.Count != 1) continue; // нет действующей — пересаживать некуда
            foreach (var t in group.Where(t => !t.IsActive)) map[t.Id] = active[0];
        }
        return map;
    }

    // Правило 1. Действующей остаётся версия с наибольшим номером, при равенстве — созданная позже,
    // как «Недавние» выбирают текущую ([ID-13]). «По умолчанию» любой версии шаблона переходит к ней —
    // оно принадлежит шаблону типа, а не строке (как в CreateNewVersion).
    private static void RetireExtraActive(List<Template> versions, List<Change> changes)
    {
        var active = versions.Where(v => v.IsActive).ToList();
        if (active.Count < 2) return;
        var anyDefault = versions.Any(v => v.IsDefault);
        var survivor = active
            .OrderByDescending(v => v.Version).ThenByDescending(v => v.CreatedAt)
            .ThenByDescending(IdText, StringComparer.Ordinal)
            .First();
        foreach (var v in versions)
        {
            if (ReferenceEquals(v, survivor) || (!v.IsActive && !v.IsDefault)) continue;
            if (v.IsActive) changes.Add(new(v, ChangeKind.Retired, v.Version, v.Version));
            v.RetireAsDuplicate();
        }
        survivor.KeepAsActive(anyDefault);
    }

    // Правило 2. Из строк с одним номером номер сохраняет самая ранняя; остальные получают max+1,
    // max+2, … в порядке появления. Номер, который люди видели, меняется только там, где он и так
    // был неоднозначен.
    private static void RenumberRepeats(List<Template> versions, List<Change> changes)
    {
        var next = versions.Max(v => v.Version);
        var late = versions.GroupBy(v => v.Version)
            .SelectMany(g => g.OrderBy(v => v.CreatedAt).ThenBy(IdText, StringComparer.Ordinal).Skip(1))
            .OrderBy(v => v.CreatedAt).ThenBy(IdText, StringComparer.Ordinal)
            .ToList();
        foreach (var v in late)
        {
            var from = v.Version;
            v.Renumber(++next);
            changes.Add(new(v, ChangeKind.Renumbered, from, next));
        }
    }

    // Порядок идентификаторов — как у uuid в PostgreSQL (по тексту), а не как у Guid.CompareTo:
    // иначе при равных номере и времени SQL миграции и эта функция выбрали бы разные строки.
    private static string IdText(Template t) => t.Id.ToString();
}
