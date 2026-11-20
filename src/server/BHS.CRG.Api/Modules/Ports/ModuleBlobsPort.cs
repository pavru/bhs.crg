using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Ports;
using Minio.Exceptions;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Файлы модуля — в общее хранилище (ТЗ CORE-34).
///
/// ⚠️ Зависимость объявлена интерфейсом <see cref="IBlobStorage" />, то есть достаётся ОБЁРТКА с
/// реестром блобов, а не сам S3-клиент. Это и есть причина существования порта: файл, сложенный
/// мимо реестра, не попадёт в поиск осиротевших и останется в хранилище навсегда. Резервная копия
/// реестр не читает: файл модуля она берёт по пути в его строке (issue #1261, <c>ModuleFileScan</c>).
///
/// <para><b>Код модуля порт знает по построению</b> (ТЗ CORE-37.2): каждый включённый модуль получает
/// свой экземпляр keyed-сервисом (<see cref="ModulePorts" />), и все вызовы идут от имени этого кода.
/// Назвать чужой код модулю нечем — параметра нет; файл чужого модуля и файл ядра для порта
/// «отсутствуют».</para>
/// </summary>
public sealed class ModuleBlobsPort(IBlobStorage blobs, string module) : IModuleBlobs
{
    public Task<string> PutAsync(string fileName, Stream content, string contentType, CancellationToken ct = default) =>
        blobs.UploadAsync(fileName, content, contentType, module, ct);

    public async Task<Stream> OpenAsync(string path, CancellationToken ct = default)
    {
        try
        {
            return await blobs.DownloadAsync(path, module, ct);
        }
        catch (ObjectNotFoundException)
        {
            // Реестр ещё знает путь, а объект потерян. Модуль не зависит от SDK хранилища; остальные отказы
            // (нет бакета, прав, связи) остаются отказами и не выглядят отсутствующим файлом.
            throw new NotFoundException("Файл не найден.");
        }
    }

    public Task DeleteAsync(string path, CancellationToken ct = default) =>
        blobs.DeleteAsync(path, module, ct);

    public Task<long?> SizeAsync(string path, CancellationToken ct = default) =>
        blobs.GetSizeAsync(path, module, ct);
}
