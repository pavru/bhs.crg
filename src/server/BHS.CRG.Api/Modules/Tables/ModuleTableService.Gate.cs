using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Tables;
using BHS.CRG.Modules.Tables;

namespace BHS.CRG.Api.Modules.Tables;

public sealed partial class ModuleTableService
{
    /// <summary>
    /// Ворота таблицы для того, кто работает не со строками, а с её сохранёнными представлениями
    /// (G3a, issue #1095). Те же ворота, что у строк и описания: представление видно тому и только
    /// тому, кому открыта таблица, — второе правило доступа разошлось бы с первым.
    /// </summary>
    public async Task<(TableGate? Gate, TableRefusal? Refusal)> GateAsync(
        string address, DataAccess access, CancellationToken ct)
    {
        var (opened, denied) = await OpenAsync(address, access, ct);
        return opened is null
            ? (null, denied)
            : (new(opened.Entry.Address, opened.Entry.Table.Title, opened.Off, opened.Entry.Table.Views ?? [],
                opened.Columns), null);
    }
}

/// <summary>Таблица, открытая спрашивающему, — без колонок и строк.</summary>
/// <param name="Address">Адрес, как его объявил модуль: им таблица названа в хранимом представлении.</param>
/// <param name="Off">Причина «модуль выключен» словами; null — модуль включён.</param>
/// <param name="ModuleViews">Готовые представления модуля — запертые.</param>
/// <param name="Columns">Все колонки таблицы; закрытые спрашивающему — с причиной.</param>
public sealed record TableGate(
    string Address, string Title, string? Off, IReadOnlyList<ModuleTableView> ModuleViews,
    IReadOnlyList<TableColumnDto> Columns);
