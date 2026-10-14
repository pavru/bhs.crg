import { inflateRawSync } from 'node:zlib';

/**
 * Текст листа выгруженной книги XLSX — без библиотек: книга — это zip, лист в нём — XML. Читаем по
 * центральному каталогу, а не по локальным заголовкам: потоковая запись ставит размеры после данных,
 * и в локальном заголовке их нет.
 */
export function sheetXml(book) {
  let end = book.length - 22;
  while (end >= 0 && book.readUInt32LE(end) !== 0x06054b50) end--;
  if (end < 0) throw new Error('файл не zip — это не книга XLSX');
  let at = book.readUInt32LE(end + 16);
  for (let i = 0; i < book.readUInt16LE(end + 10); i++) {
    const [method, size, nameLength] = [book.readUInt16LE(at + 10), book.readUInt32LE(at + 20), book.readUInt16LE(at + 28)];
    const name = book.toString('utf8', at + 46, at + 46 + nameLength);
    const local = book.readUInt32LE(at + 42);
    if (name === 'xl/worksheets/sheet1.xml') {
      const start = local + 30 + book.readUInt16LE(local + 26) + book.readUInt16LE(local + 28);
      const data = book.subarray(start, start + size);
      // Не-латиница в листе записана числовыми ссылками («&#1057;») — раскрываем, чтобы искать словами.
      return (method === 8 ? inflateRawSync(data) : data).toString('utf8')
        .replace(/&#(\d+);/g, (_, code) => String.fromCodePoint(Number(code)));
    }
    at += 46 + nameLength + book.readUInt16LE(at + 30) + book.readUInt16LE(at + 32);
  }
  throw new Error('листа в книге нет');
}
