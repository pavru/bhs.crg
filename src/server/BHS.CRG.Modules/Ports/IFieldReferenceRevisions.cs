namespace BHS.CRG.Modules.Ports;

/// <summary>Зафиксированные ревизии источников полевого снимка. Содержимое справочников не читается.</summary>
public interface IFieldReferenceRevisions
{
    Task<string> ReadAsync(string kind, CancellationToken ct);
}
