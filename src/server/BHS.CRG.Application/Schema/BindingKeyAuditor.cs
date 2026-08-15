using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Schema;

/// <summary>
/// Аудит ДЕРЖАТЕЛЕЙ КЛЮЧЕЙ ПОЛЕЙ, живущих вне реквизитов (issue #737).
///
/// <para><see cref="SchemaDataAuditor"/> сверяет со схемой сами данные объекта. Но ключ поля
/// хранится не только там: на него ссылаются привязки наборов данных (<c>TargetFieldKey</c> и ключи
/// маппинга) и шаблоны привязок типа. Переименуй поле — и эти ссылки осиротеют, оставаясь
/// невидимыми: аудит смотрел в данные, а разошлась настройка.</para>
///
/// <para>Живой случай, ради которого это заведено: в реестре исполнительной документации поле
/// переименовали «ОсновнойДокументы» → «ОсновныеДокументы», человек завёл привязку заново, старая
/// осталась — и продолжала наливать устаревшие данные в мёртвый ключ. В <c>data.json</c> они
/// попадали, аудит инстанса молчал.</para>
///
/// <para>Чистая функция: на вход — уже прочитанные привязки и справочник типов, на выход — находки
/// того же вида, что у аудитора данных. Отдельным классом, а не веткой внутри
/// <see cref="SchemaDataAuditor"/>, потому что тот сознательно ничего не знает о наборах данных —
/// он работает над одним JSON-документом.</para>
/// </summary>
public static class BindingKeyAuditor
{
    /// <summary>Привязка указывает на поле, которого нет в эффективной схеме владельца.</summary>
    public const string OrphanBinding = "orphan-binding";

    /// <summary>То же для шаблона привязок типа документа.</summary>
    public const string OrphanBindingTemplate = "orphan-binding-template";

    /// <summary>
    /// Находки по привязкам одного владельца против эффективной схемы его типа.
    ///
    /// <para>Скалярная привязка (<c>TargetFieldKey == null</c>) — легальный режим: целевых полей у
    /// неё несколько, и перечисляет их маппинг. Поэтому проверяются либо один целевой ключ, либо
    /// ключи маппинга, но не «пустой ключ» как таковой — пустой ключ здесь не поломка.</para>
    /// </summary>
    public static IReadOnlyList<AuditIssue> AuditBindings(
        IEnumerable<DataSetBindingDto> bindings, Guid typeId,
        IReadOnlyDictionary<Guid, DocumentType> byId)
    {
        var known = DocumentTypeSchemaReader.EffectiveFields(typeId, byId).Select(f => f.Key).ToHashSet();
        var issues = new List<AuditIssue>();

        foreach (var b in bindings)
        {
            var source = b.Source?.Name ?? "без имени";
            if (b.TargetFieldKey is { } key)
            {
                if (!known.Contains(key))
                    issues.Add(new(OrphanBinding, AuditSeverity.Warning, key,
                        $"Привязка источника «{source}» указывает на поле «{key}», которого нет в схеме типа — "
                        + "данные в документ не попадают."));
                continue;
            }

            // Скалярная: целевые поля перечислены в маппинге.
            foreach (var mapKey in b.Mapping.Keys.Where(k => !known.Contains(k)))
                issues.Add(new(OrphanBinding, AuditSeverity.Warning, mapKey,
                    $"Маппинг привязки источника «{source}» указывает на поле «{mapKey}», которого нет в схеме типа — "
                    + "значение не записывается."));
        }

        return issues;
    }

    /// <summary>
    /// Находки по шаблонам привязок типа. Шаблон не заполняет документ сам — он заготовка, по
    /// которой привязку создают. Поэтому осиротевший ключ здесь не портит данные сегодня, но
    /// гарантирует, что следующая созданная по нему привязка окажется мёртвой с рождения.
    /// </summary>
    public static IReadOnlyList<AuditIssue> AuditTemplates(
        IEnumerable<DataSetBindingTemplateDto> templates, Guid typeId,
        IReadOnlyDictionary<Guid, DocumentType> byId)
    {
        var known = DocumentTypeSchemaReader.EffectiveFields(typeId, byId).Select(f => f.Key).ToHashSet();
        var issues = new List<AuditIssue>();

        foreach (var t in templates)
        {
            if (t.TargetFieldKey is { } key)
            {
                if (!known.Contains(key))
                    issues.Add(new(OrphanBindingTemplate, AuditSeverity.Warning, key,
                        $"Шаблон привязки «{t.Name}» нацелен на поле «{key}», которого нет в схеме типа."));
                continue;
            }

            foreach (var mapKey in t.ColumnMappings.Keys.Where(k => !known.Contains(k)))
                issues.Add(new(OrphanBindingTemplate, AuditSeverity.Warning, mapKey,
                    $"Шаблон привязки «{t.Name}» ссылается на поле «{mapKey}», которого нет в схеме типа."));
        }

        return issues;
    }
}
