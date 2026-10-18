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
/// <see cref="BackupManifestFieldMap.NestedRecords" />. Имя в копии бывает другим — тогда пара названа в
/// <see cref="BackupManifestFieldMap.Renamed" />. Проверяется присутствие поля, а не то, что экспорт его заполняет: это
/// делают тесты самой копии.</para>
/// </summary>
[Collection("Integration")]
public class BackupManifestFieldCoverageTests(IntegrationTestFixture fixture)
{
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
        ["DomainObject.SearchText"] = "генерируемая колонка поиска (issue #1247): её считает база из названия и имён",
        // Представления таблиц (issue #1095): в копию идут только ОБЩИЕ, поэтому признак «общее» у
        // каждой записи копии один и тот же; автор не едет — учётные записи копия не переносит.
        ["SavedTableView.Shared"] = "в копии только общие представления: признак у всех записей один",
        ["SavedTableView.OwnerId"] = "автор — учётная запись, а их копия не переносит; на новом месте автора нет",
        ["SavedTableView.RowVersion"] = "системная колонка xmin: её ведёт база, перенести нельзя и незачем",
    };

    private List<(string Key, bool Present)> Fields()
    {
        using var scope = fixture.Services.CreateScope();
        return BackupManifestFieldMap.Of(scope.ServiceProvider.GetRequiredService<AppDbContext>())
            .Select(f => (f.Key, f.InRecord is not null)).ToList();
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
            "DeliberatelyOmitted с причиной; если в копии оно под другим именем — в BackupManifestFieldMap.Renamed. Молча " +
            "оставлять нельзя: значение потеряется при восстановлении, и узнают об этом после аварии.");
    }

    /// <summary>Обратная сторона: строка переписи, за которой больше нет поля или причины.</summary>
    [Fact]
    public void Inventory_HoldsNoDeadRows()
    {
        var fields = Fields();
        var known = fields.Select(f => f.Key).ToHashSet();
        var present = fields.Where(f => f.Present).Select(f => f.Key).ToHashSet();

        var dead = DeliberatelyOmitted.Keys.Concat(BackupManifestFieldMap.Renamed.Keys).Where(k => !known.Contains(k))
            .Select(k => k + " — такого поля в модели нет")
            // Поле, названное пропущенным, а в копии оно есть: исключение устарело и прикрывает
            // следующую потерю под тем же именем.
            .Concat(DeliberatelyOmitted.Keys.Where(present.Contains).Select(k => k + " — в копии оно уже есть"))
            // Переименование, ведущее в никуда.
            .Concat(BackupManifestFieldMap.Renamed.Keys.Where(k => known.Contains(k) && !present.Contains(k))
                .Select(k => k + " — свойства «" + BackupManifestFieldMap.Renamed[k] + "» в записи копии нет"))
            .OrderBy(k => k).ToList();

        Assert.True(dead.Count == 0, "В переписи полей копии — устаревшие строки:\n  " + string.Join("\n  ", dead));
    }
}
