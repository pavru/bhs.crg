using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.DataSets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Правки источника, собранные по его копии на странице, — и то, чем они защищены от затирания чужой
/// правки (issue #1141): сверка версии и блокировка строки на время «сверил — записал».
///
/// <para>Три таких правки: обработка (<c>DataSetSourceService.Processing.cs</c>), извлечение и
/// материализация (обе здесь). У каждой диалог заполнен из копии источника, и устаревшая копия молча
/// возвращала прежнее значение туда, где другой человек только что поставил своё. Переименование,
/// тэги и копия сюда не относятся: они не строятся на прежнем состоянии.</para>
///
/// <para>Своим файлом по той же причине, что соседние части: основной стоит в храповике размера.</para>
/// </summary>
public partial class DataSetSourceService
{
    /// <summary>
    /// Блокирует строку источника до конца транзакции и перечитывает его: после этого сверка версии и
    /// запись неразрывны. Без блокировки два сохранения, пришедшие почти разом, оба читали источник,
    /// оба проходили сверку и оба писали — побеждало последнее, и оба получали «сохранено».
    ///
    /// <para>Зовётся ПОСЛЕ долгих проверок (отбор спрашивает поставщика, извлечение читает файл), а не
    /// до них: строку источника правят и распознавание, и кэш схемы, и держать их в очереди, пока
    /// собирается консолидация, незачем. Поэтому версию сверяют дважды: сразу после чтения — чтобы
    /// устаревшая правка получила отказ, не дожидаясь проверок, — и ещё раз здесь, под блокировкой.</para>
    ///
    /// <para>Перечитывание нужно и правке без версии: части обработки, которых в запросе нет, берутся
    /// из сохранённого, и сохранённым обязано быть то, что лежит в базе сейчас, а не то, что было в
    /// ней секунду назад.</para>
    /// </summary>
    private async Task<IDbContextTransaction> LockAsync(DataSetSource source, CancellationToken ct)
    {
        // Имена таблицы и колонки — из модели, а не строкой: переименует их миграция — запрос уедет следом.
        var entity = db.Model.FindEntityType(typeof(DataSetSource))!;
        var stored = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        var quote = db.GetService<ISqlGenerationHelper>();
        var table = quote.DelimitIdentifier(stored.Name, stored.Schema);
        var id = quote.DelimitIdentifier(entity.FindProperty(nameof(DataSetSource.Id))!.GetColumnName(stored)!);

        var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync($"SELECT 1 FROM {table} WHERE {id} = {{0}} FOR UPDATE", [source.Id], ct);
            await db.Entry(source).ReloadAsync(ct);
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    /// <summary>Отказ правке, собранной по прежней копии источника. <paramref name="notSaved" /> —
    /// что именно не сохранено, с именем источника.</summary>
    private static ConflictException SourceMoved(string notSaved) => new(
        $"{notSaved}: источник тем временем изменили — правка собрана по прежней копии, и сохранение "
        + "затёрло бы чужую. Обновите страницу и повторите правку: диалог покажет источник, каким он стал.");

    /// <summary>
    /// Настроить/снять материализацию источника в тип (issue #19): typeId=null снимает. Настройка
    /// задаётся целиком: тип, маппинг и (issue #716) правило выбора варианта.
    /// Сохраняется ЗАМЕЩЕНИЕМ — частичных правок здесь нет намеренно: маппинг и правила связаны, и
    /// сохранить одно без другого значит оставить источник в состоянии, которого валидатор не пропустил бы.
    ///
    /// <para>Замещение целиком и делает устаревшую копию опасной: диалог, открытый до чужой правки,
    /// вернул бы прежнюю настройку. <paramref name="ifMatch" /> — версия материализации
    /// (<see cref="SourceProcessingVersion.OfMaterialization" />), которую показывала страница.</para>
    /// </summary>
    public async Task<DataSetSourceDto?> SetMaterializationAsync(
        Guid sourceId, Guid? typeId, Dictionary<string, string>? mapping,
        MaterializeDiscriminatorConfig? discriminator, string? byIdColumn, CancellationToken ct,
        string? ifMatch = null)
    {
        var source = await db.DataSetSources.FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;
        EnsureMaterializationCurrent(source, ifMatch);

        var effectiveMapping = mapping ?? new Dictionary<string, string>();
        if (typeId is { } id)
        {
            var typesById = await db.DocumentTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
            if (!typesById.TryGetValue(id, out var type))
                throw new NotFoundException($"Тип {id} не найден.");
            MaterializeConfigValidator.Validate(type, effectiveMapping, discriminator, typesById, byIdColumn);
        }

        var mappingJson = typeId is null ? null : JsonSerializer.Serialize(effectiveMapping);
        var discriminatorJson = typeId is null || discriminator is null
            ? null
            : JsonSerializer.Serialize(discriminator);

        await using var transaction = await LockAsync(source, ct);
        EnsureMaterializationCurrent(source, ifMatch);
        source.SetMaterialization(typeId, mappingJson, discriminatorJson, byIdColumn);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return DataSetDtoMapper.MapSource(source);
    }

    private static void EnsureMaterializationCurrent(DataSetSource source, string? ifMatch)
    {
        if (SourceProcessingVersion.MaterializationMoved(source, ifMatch))
            throw SourceMoved($"Материализация источника «{source.Name}» не сохранена");
    }

    /// <summary>
    /// Ручная правка извлечения: имя, локатор, колонки. <c>IfMatch</c> во входе — версия обработки
    /// (она включает извлечение), с которой открыт редактор: локатор и колонки он показывает из копии
    /// источника на странице, и сохранение с устаревшей вернуло бы прежнее извлечение.
    /// </summary>
    public async Task<DataSetSourceDto?> UpdateSourceAsync(Guid sourceId, UpdateSourceInput input, CancellationToken ct)
    {
        var source = await db.DataSetSources.Include(s => s.File).FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;
        // Определение системного источника — это выбор консолидации, менять в нём нечего: переименование
        // идёт через RenameSourceAsync, а другая консолидация — другой источник.
        if (source.File.IsSystem)
            throw new InvalidRequestException("Определение системного источника не редактируется — переименуйте его или создайте другой.");
        if (string.IsNullOrWhiteSpace(input.Name)) throw new InvalidRequestException("Укажите название источника.");
        EnsureDefinitionCurrent(source, input.IfMatch);
        await EnsureNameFreeAsync(source.FileId, input.Name.Trim(), sourceId, ct, source.Name);

        var columnExpressionsJson = DataSetDtoMapper.SerializeColumnExpressions(input.ColumnExpressions);
        var (schema, rowCount) = await ParseForDefinitionAsync(
            source.File.BlobPath, source.File.Format, input.SheetOrPath, columnExpressionsJson, ct);

        await using var transaction = await LockAsync(source, ct);
        EnsureDefinitionCurrent(source, input.IfMatch);
        source.UpdateDefinition(input.Name.Trim(), input.SheetOrPath.Trim(), columnExpressionsJson);
        source.UpdateCache(DataSetDtoMapper.SerializeSchema(schema), rowCount);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return DataSetDtoMapper.MapSource(source);
    }

    private static void EnsureDefinitionCurrent(DataSetSource source, string? ifMatch)
    {
        if (SourceProcessingVersion.Moved(source, ifMatch))
            throw SourceMoved($"Источник «{source.Name}» не сохранён");
    }
}
