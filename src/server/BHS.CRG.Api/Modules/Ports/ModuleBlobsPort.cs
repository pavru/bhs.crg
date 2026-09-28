using BHS.CRG.Application.Common;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Файлы модуля — в общее хранилище (ТЗ CORE-34).
///
/// ⚠️ Зависимость объявлена интерфейсом <see cref="IBlobStorage" />, то есть достаётся ОБЁРТКА с
/// реестром блобов, а не сам S3-клиент. Это и есть причина существования порта: файл, сложенный
/// мимо реестра, не попадёт ни в оценку веса резервной копии, ни в поиск осиротевших — и обнаружится
/// как «копия меньше, чем данных».
/// </summary>
public sealed class ModuleBlobsPort(IBlobStorage blobs) : IModuleBlobs
{
    public Task<string> PutAsync(string fileName, Stream content, string contentType, CancellationToken ct = default) =>
        blobs.UploadAsync(fileName, content, contentType, ct);

    public Task<Stream> OpenAsync(string path, CancellationToken ct = default) =>
        blobs.DownloadAsync(path, ct);

    public Task DeleteAsync(string path, CancellationToken ct = default) =>
        blobs.DeleteAsync(path, ct);

    public Task<long?> SizeAsync(string path, CancellationToken ct = default) =>
        blobs.GetSizeAsync(path, ct);
}
