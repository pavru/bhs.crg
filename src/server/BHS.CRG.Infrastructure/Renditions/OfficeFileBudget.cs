using System.IO.Compression;
using System.Text;

namespace BHS.CRG.Infrastructure.Renditions;

/// <summary>
/// Бюджет офисного файла ДО конвертера (issue #1268): то, что можно узнать о файле, не открывая его
/// программой, которая умеет зависать и съедать память.
///
/// <para><c>.xlsx</c> и <c>.docx</c> — архивы, и размер файла о них не говорит ничего: сотня килобайт
/// разворачивается в гигабайты. Заявленному в оглавлении размеру не верим тоже — его пишет тот, кто
/// собирал файл; записи читаются на самом деле, и чтение обрывается на пределе.</para>
/// </summary>
internal static class OfficeFileBudget
{
    private static readonly byte[] ZipSign = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] OleSign = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>
    /// Имя потока, в котором лежит зашифрованная книга или документ: файл под паролем — уже не
    /// архив, а контейнер OLE с этим потоком. В каталоге контейнера имена записаны в UTF-16.
    /// </summary>
    private static readonly byte[] EncryptedPackage = Encoding.Unicode.GetBytes("EncryptedPackage");

    /// <summary>Почему файл не пойдёт к конвертеру; <c>null</c> — в бюджет уложился.</summary>
    public static Rendition.Refused? Check(OfficeFormat format, byte[] file)
    {
        if (file.LongLength > RenditionLimits.OfficeMaxBytes)
            return TooLarge($"он больше {RenditionLimits.OfficeMaxBytes / (1024 * 1024)} МБ");

        // Старый Excel — не архив, а контейнер: раздуваться в нём нечему.
        return format is OfficeFormat.Xlsx or OfficeFormat.Docx ? CheckArchive(file) : null;
    }

    /// <summary>
    /// Что сказать о файле, вид которого не определился: защищён, повреждён или просто чужой.
    /// Разница человеку важна — с первым и вторым он знает, что делать.
    /// </summary>
    public static Rendition.Refused? WhyUnknown(ReadOnlySpan<byte> file)
    {
        if (file.StartsWith(OleSign) && file.IndexOf(EncryptedPackage) >= 0)
            return new(RenditionRefusal.Protected,
                "Файл защищён паролем — привести его к читаемому виду нельзя. Снимите пароль и приложите файл заново.");
        if (file.StartsWith(ZipSign) && !OpensAsArchive(file))
            return new(RenditionRefusal.Corrupted,
                "Файл повреждён: он начинается как офисный, но прочитать его нельзя.");
        return null;
    }

    private static Rendition.Refused? CheckArchive(byte[] file)
    {
        // Предел распакованного — меньший из двух: абсолютный и «во столько-то раз больше файла».
        var cap = Math.Min(RenditionLimits.ArchiveMaxUnpackedBytes,
            Math.Max(RenditionLimits.ArchiveRatioFloorBytes, file.LongLength * RenditionLimits.ArchiveMaxRatio));
        try
        {
            using var zip = new ZipArchive(new MemoryStream(file, writable: false), ZipArchiveMode.Read);
            if (zip.Entries.Count > RenditionLimits.ArchiveMaxEntries)
                return TooLarge($"в нём больше {RenditionLimits.ArchiveMaxEntries} частей");

            long unpacked = 0;
            var buffer = new byte[81920];
            foreach (var entry in zip.Entries)
            {
                using var part = entry.Open();
                int read;
                while ((read = part.Read(buffer, 0, buffer.Length)) > 0)
                {
                    unpacked += read;
                    if (unpacked > cap) return TooLarge("в распакованном виде он слишком велик");
                }
            }
            return null;
        }
        catch (InvalidDataException)
        {
            return new(RenditionRefusal.Corrupted, "Файл повреждён: его содержимое не читается.");
        }
    }

    private static bool OpensAsArchive(ReadOnlySpan<byte> file)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(file.ToArray(), writable: false), ZipArchiveMode.Read);
            return zip.Entries.Count >= 0;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static Rendition.Refused TooLarge(string why) =>
        new(RenditionRefusal.TooLarge, $"Файл слишком велик для читаемого вида: {why}.");
}
