using BHS.CRG.Application.Backup;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Каждое ПОЛЕ сущности, которую переносит резервная копия, либо есть в её записи манифеста, либо
/// названо здесь с причиной (issue #1185).
///
/// <para>Соседний <see cref="BackupManifestCoverageTests" /> стережёт сущности: новая таблица без
/// решения о копии — отказ. О полях он не знает ничего, и новая колонка существующей сущности в копию
/// не попадала молча: сборка зелёная, копия снимается, восстановление проходит — а значение
/// теряется. Так потерялся бы признак архива записи: восстановили копию — и всё, что убрали из
/// выбора, вернулось.</para>
///
/// <para>Сверка — по именам: колонка модели EF против свойств записи манифеста ИМЕННО этой
/// сущности. Вложенные записи в общий котёл не сливаются: у документа и его выпущенного файла есть
/// одноимённые поля, и поле файла, закрытое одноимённым полем документа, терялось бы при зелёном
/// стороже (ревью PR #1216). Сущность, которая едет вложенной, называет свою запись в
/// <see cref="NestedRecords" />. Имя в копии бывает другим — тогда пара названа в
/// <see cref="Renamed" />. Проверяется присутствие поля, а не то, что экспорт его заполняет: это
/// делают тесты самой копии.</para>
/// </summary>
[Collection("Integration")]
public class BackupManifestFieldCoverageTests(IntegrationTestFixture fixture)
{
    /// <summary>Сущности, которые едут ВНУТРИ записи другой сущности, и их собственная запись.</summary>
    private static readonly Dictionary<string, Type> NestedRecords = new()
    {
        ["GeneratedFile"] = typeof(BackupGeneratedFile),
    };

    /// <summary>Поле сущности → свойство записи манифеста, когда имена разные.</summary>
    private static readonly Dictionary<string, string> Renamed = new()
    {
        ["DomainObject.ScopeLevel"] = nameof(BackupCommonDataEntry.Scope),
        // Фасета едет одной записью с документом: её ключ — идентификатор документа.
        ["DocumentFacet.ObjectId"] = nameof(BackupDocument.Id),
    };

    /// <summary>Поля, которых в копии нет СОЗНАТЕЛЬНО, и почему.</summary>
    private static readonly Dictionary<string, string> DeliberatelyOmitted = new()
    {
        ["GeneratedFile.ObjectId"] = "владельца называет вложенность: файл лежит внутри записи своего документа",
        ["TypstUserLib.Id"] = "запись одна на систему, идентификатор постоянный (TypstUserLib.SingletonId)",
        // Признак «заводской профиль с тех пор обновился» сидер выставляет сам, сравнивая BuiltInHash
        // (он в копии есть) с хэшем этой сборки. В копии он был бы слепком чужой сборки.
        ["RecognitionProfile.BuiltInOutdated"] = "производное: сидер пересчитывает по BuiltInHash при старте",
        // Версия строки (issue #1214) — системная колонка базы: у восстановленной записи она своя,
        // и форма называет ту, что прочла после восстановления.
        ["DomainObject.RowVersion"] = "системная колонка xmin: её ведёт база, перенести нельзя и незачем",
    };

    private static string NameOf(Type t) =>
        t.Name.IndexOf('`') is var i && i > 0 ? t.Name[..i] : t.Name;

    /// <summary>Тип записи манифеста: элемент массива или списка, либо само свойство.</summary>
    private static Type RecordOf(Type t) =>
        t.IsArray ? t.GetElementType()!
        : t.IsGenericType && t.GetGenericArguments() is [var arg] && arg.Namespace == typeof(BackupManifest).Namespace ? arg
        : Nullable.GetUnderlyingType(t) ?? t;

    /// <summary>Имена свойств записи манифеста — её собственных, без вложенных записей.</summary>
    private static HashSet<string> FieldsOf(Type record) =>
        record.GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

    private List<(string Key, bool Present)> Fields()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var result = new List<(string, bool)>();
        foreach (var entity in db.Model.GetEntityTypes())
        {
            var name = NameOf(entity.ClrType);
            if (!BackupManifestCoverageTests.CoveredByManifest.TryGetValue(name, out var section)) continue;
            var fields = FieldsOf(NestedRecords.TryGetValue(name, out var nested)
                ? nested
                : RecordOf(typeof(BackupManifest).GetProperty(section)!.PropertyType));
            foreach (var property in entity.GetProperties())
            {
                var key = $"{name}.{property.Name}";
                var present = fields.Contains(Renamed.TryGetValue(key, out var other) ? other : property.Name);
                result.Add((key, present));
            }
        }
        return result;
    }

    [Fact]
    public void EveryField_IsEitherInManifestRecord_OrExplicitlyOmitted()
    {
        var undecided = Fields()
            .Where(f => !f.Present && !DeliberatelyOmitted.ContainsKey(f.Key))
            .Select(f => f.Key).OrderBy(k => k).ToList();

        Assert.True(undecided.Count == 0,
            "У сущностей, которые переносит копия, есть поля без решения о копии:\n  " +
            string.Join("\n  ", undecided) + "\n" +
            "Добавьте каждое ЛИБО в запись манифеста (и в сборку с восстановлением), ЛИБО в " +
            "DeliberatelyOmitted с причиной; если в копии оно под другим именем — в Renamed. Молча " +
            "оставлять нельзя: значение потеряется при восстановлении, и узнают об этом после аварии.");
    }

    /// <summary>Обратная сторона: строка переписи, за которой больше нет поля или причины.</summary>
    [Fact]
    public void Inventory_HoldsNoDeadRows()
    {
        var fields = Fields();
        var known = fields.Select(f => f.Key).ToHashSet();
        var present = fields.Where(f => f.Present).Select(f => f.Key).ToHashSet();

        var dead = DeliberatelyOmitted.Keys.Concat(Renamed.Keys).Where(k => !known.Contains(k))
            .Select(k => k + " — такого поля в модели нет")
            // Поле, названное пропущенным, а в копии оно есть: исключение устарело и прикрывает
            // следующую потерю под тем же именем.
            .Concat(DeliberatelyOmitted.Keys.Where(present.Contains).Select(k => k + " — в копии оно уже есть"))
            // Переименование, ведущее в никуда.
            .Concat(Renamed.Keys.Where(k => known.Contains(k) && !present.Contains(k))
                .Select(k => k + " — свойства «" + Renamed[k] + "» в записи копии нет"))
            .OrderBy(k => k).ToList();

        Assert.True(dead.Count == 0, "В переписи полей копии — устаревшие строки:\n  " + string.Join("\n  ", dead));
    }
}
