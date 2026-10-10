import { describe, expect, it } from 'vitest';
import { scanShownAs } from './scanView';

describe('scanShownAs', () => {
  it('PDF и растровые изображения показываются', () => {
    expect(scanShownAs('application/pdf')).toBe('pdf');
    expect(scanShownAs('Application/PDF; qs=0.9')).toBe('pdf');
    expect(scanShownAs('image/png')).toBe('image');
    expect(scanShownAs('image/jpeg')).toBe('image');
    expect(scanShownAs('image/webp')).toBe('image');
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
