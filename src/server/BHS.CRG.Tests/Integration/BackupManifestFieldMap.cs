using System.Reflection;
using BHS.CRG.Application.Backup;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Поле сущности, которую переносит копия, ↔ свойство её записи в манифесте (issue #1185, #1422).
///
/// <para>Общее для двух сторожей: <see cref="BackupManifestFieldCoverageTests" /> проверяет, что
/// поле в копии есть, <see cref="BackupManifestFileHoldersTests" /> — что у строковых JSON-полей есть
/// решение о файлах. Сопоставление одно, чтобы сторожа не разошлись в том, какое поле чем считается.</para>
///
/// <para>Сверка — по именам: колонка модели EF против свойств записи манифеста ИМЕННО этой сущности.
/// Вложенные записи в общий котёл не сливаются (ревью PR #1216).</para>
/// </summary>
internal static class BackupManifestFieldMap
{
    /// <summary>Сущности, которые едут ВНУТРИ записи другой сущности, и их собственная запись.</summary>
    internal static readonly Dictionary<string, Type> NestedRecords = new()
    {
        ["GeneratedFile"] = typeof(BackupGeneratedFile),
    };

    /// <summary>Поле сущности → свойство записи манифеста, когда имена разные.</summary>
    internal static readonly Dictionary<string, string> Renamed = new()
    {
        ["DomainObject.ScopeLevel"] = nameof(BackupCommonDataEntry.Scope),
        // Фасета едет одной записью с документом: её ключ — идентификатор документа.
        ["DocumentFacet.ObjectId"] = nameof(BackupDocument.Id),
    };

    /// <summary>Поле сущности и свойство записи манифеста, которым оно едет (<c>null</c> — не едет).</summary>
    internal sealed record Field(string Key, IProperty Property, Type Record, PropertyInfo? InRecord);

    /// <summary>Имя типа без арности (<c>IdentityRole`1</c> → <c>IdentityRole</c>).</summary>
    private static string NameOf(Type t) =>
        t.Name.IndexOf('`') is var i && i > 0 ? t.Name[..i] : t.Name;

    /// <summary>Тип записи манифеста: элемент массива или списка, либо само свойство.</summary>
    private static Type RecordOf(Type t) =>
        t.IsArray ? t.GetElementType()!
        : t.IsGenericType && t.GetGenericArguments() is [var arg] && arg.Namespace == typeof(BackupManifest).Namespace ? arg
        : Nullable.GetUnderlyingType(t) ?? t;

    internal static List<Field> Of(AppDbContext db)
    {
        var result = new List<Field>();
        foreach (var entity in db.Model.GetEntityTypes())
        {
            var name = NameOf(entity.ClrType);
            if (!BackupManifestCoverageTests.CoveredByManifest.TryGetValue(name, out var section)) continue;
            var record = NestedRecords.TryGetValue(name, out var nested)
                ? nested
                : RecordOf(typeof(BackupManifest).GetProperty(section)!.PropertyType);
            foreach (var property in entity.GetProperties())
            {
                var key = $"{name}.{property.Name}";
                var inRecord = record.GetProperty(Renamed.TryGetValue(key, out var other) ? other : property.Name);
                result.Add(new Field(key, property, record, inRecord));
            }
        }
        return result;
    }
}
