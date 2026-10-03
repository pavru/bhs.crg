using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.Catalog;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Системный набор в источниках: кандидаты консолидаций и создание источника на них
/// (issue #580, #606; ворота доступа — issue #965).
///
/// <para>Своим файлом, а не строками в <c>DataSetSourceService.cs</c>: тот стоит в храповике размера
/// (571 строка кода), и параметр доступа добавил бы ему ещё. Разрез здесь не формальный — это ровно
/// та половина, где живут ворота опубликованного набора (ТЗ CORE-24.1): «предложить» и «создать»
/// оба ЧИТАЮТ строки, и решения у них разные — кандидат недоступного набора пропускается молча, а
/// создание отказывает. Держать эти два решения рядом важнее, чем рядом с остальным CRUD.</para>
/// </summary>
public partial class DataSetSourceService
{
    /// <summary>
    /// Какие консолидации данных системы возможны на уровне — ДО создания набора (issue #606).
    /// Нужно, чтобы не предлагать системный набор там, где предложить нечего: «Документы комплекта»
    /// осмысленны только внутри комплекта, и на уровне раздела пользователь иначе упирался бы в
    /// пустой список источников.
    /// </summary>
    public async Task<IReadOnlyList<DataSetSourceInfo>> ListSystemCandidatesAsync(
        CatalogScope scope, Guid? scopeId, DataAccess access, CancellationToken ct)
    {
        var candidates = new List<DataSetSourceInfo>();
        foreach (var provider in systemProviders.All)
        {
            // Недоступный набор не ПРЕДЛАГАЕТСЯ вовсе (ТЗ CORE-24.1, issue #965). Пропуск, а не отказ:
            // список кандидатов собирается из всех поставщиков разом, и один закрытый набор не
            // должен отменять остальные. А показать его нельзя — кандидат несёт число своих строк,
            // то есть само предложение уже было бы чтением.
            if (!SystemDataSetGate.Allows(provider.Declaration, access)) continue;
            candidates.AddRange(await provider.GetCandidatesAsync(scope, scopeId, access, ct));
        }
        return candidates;
    }

    // Источник системного набора (issue #580): маркер выбирает провайдера консолидации. CachedData не
    // пишем — строки собираются заново при каждом обращении, иначе реестр отстанет от состава комплекта.
    private async Task<DataSetSourceDto> CreateSystemSourceAsync(
        Domain.DataSets.DataSetFile file, string name, string marker, DataAccess access, CancellationToken ct)
    {
        var provider = systemProviders.Get(marker);
        // Ворота и на создании (ТЗ CORE-24.1): создание прогоняет консолидацию, чтобы записать схему
        // колонок и счётчик строк, — то есть читает данные. Здесь отказ, а не пропуск: набор выбрал
        // человек, и молчаливо создать источник без строк значило бы отдать ему пустую таблицу.
        SystemDataSetGate.Ensure(provider.Declaration, access, name);
        var provided = await provider.ProvideAsync(marker, file.Scope, file.ScopeId, access, ct);

        var source = file.AddSource(name, marker, DataSetDtoMapper.SerializeSchema(provided.Columns), provided.Rows.Count);
        db.DataSetSources.Add(source);
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapSource(source);
    }
}
