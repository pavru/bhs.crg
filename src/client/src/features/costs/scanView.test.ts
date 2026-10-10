import { describe, expect, it } from 'vitest';
import { scanSaveName, scanShownAs } from './scanView';

describe('scanShownAs', () => {
  it('PDF и растровые изображения показываются', () => {
    expect(scanShownAs('application/pdf')).toBe('pdf');
    expect(scanShownAs('image/png')).toBe('image');
    expect(scanShownAs('image/jpeg')).toBe('image');
    expect(scanShownAs('image/webp')).toBe('image');
    expect(scanShownAs('image/bmp')).toBe('image');
  });

  it('всё, чего в перечне нет, только скачивается', () => {
    expect(scanShownAs('application/octet-stream')).toBe('download');
    expect(scanShownAs('text/html')).toBe('download');
    expect(scanShownAs('application/vnd.openxmlformats-officedocument.spreadsheetml.sheet')).toBe('download');
    expect(scanShownAs('')).toBe('download');
    expect(scanShownAs(null)).toBe('download');
  });

  // Сторож перечня: «начинается с image/» пропустило бы документ со сценариями как картинку, а
  // «содержит pdf» — вид, которого сервер не называл.
  it('вид, похожий на известный, известным не считается', () => {
    expect(scanShownAs('image/svg+xml')).toBe('download');
    expect(scanShownAs('application/x-pdf-like')).toBe('download');
    expect(scanShownAs('image/tiff')).toBe('download');
  });
});

describe('scanSaveName', () => {
  it('имя файла берётся как есть', () => {
    expect(scanSaveName('Счёт 417.xlsx', 'application/octet-stream')).toBe('Счёт 417.xlsx');
  });

  it('файлу без имени расширение даёт вид, а неизвестному виду — ничего', () => {
    expect(scanSaveName(null, 'application/pdf')).toBe('Скан счёта.pdf');
    expect(scanSaveName('', 'application/vnd.ms-excel')).toBe('Скан счёта.xls');
    expect(scanSaveName(null, 'application/octet-stream')).toBe('Скан счёта');
  });
});
