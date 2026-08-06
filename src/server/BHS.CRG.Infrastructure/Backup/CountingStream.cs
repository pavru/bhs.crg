namespace BHS.CRG.Infrastructure.Backup;

/// <summary>
/// Поток, который ничего не пишет и только считает байты (issue #711).
///
/// Нужен, чтобы узнать сжатый размер манифеста, не собирая копию: сериализуем его через Deflate с
/// тем же уровнем, с каким он ляжет в архив, и смотрим, сколько получилось. Считать по несжатому
/// объёму было бы проще и неверно — в общих данных лежат картинки в base64, и оценка завышала бы
/// вес в разы, то есть тревога о переполнении приходила бы задолго до повода.
///
/// Отдельным файлом, а не вложенным классом: чтение и перемотка здесь честно не поддержаны, а
/// <c>NotSupportedException</c> в файле <c>BackupService.cs</c> вывел бы весь сервис из-под политики
/// доменных отказов — там она нужна, восстановление отвечает пользователю своими словами.
/// </summary>
internal sealed class CountingStream : Stream
{
    public long Written { get; private set; }

    public override void Write(byte[] buffer, int offset, int count) => Written += count;
    public override void Write(ReadOnlySpan<byte> buffer) => Written += buffer.Length;
    public override void WriteByte(byte value) => Written++;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => Written;
    public override long Position { get => Written; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
