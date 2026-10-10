import { describe, expect, it } from 'vitest';
import {
  acceptOf, fileExtension, fileShownAs, recognizedKinds, shownKinds, wordsOf, type FileKindsInfo,
} from './fileKinds';

/** Реестр в том виде, в каком его отдаёт сервер. */
const info: FileKindsInfo = {
  unknown: 'application/octet-stream',
  maxBytes: 50 * 1024 * 1024,
  kinds: [
    { mime: 'application/pdf', label: 'PDF', extensions: ['.pdf'], view: 'pdf', recognized: true },
    { mime: 'image/png', label: 'PNG', extensions: ['.png'], view: 'image', recognized: true },
    { mime: 'image/jpeg', label: 'JPEG', extensions: ['.jpg', '.jpeg'], view: 'image', recognized: true },
    { mime: 'image/webp', label: 'WebP', extensions: ['.webp'], view: 'image', recognized: false },
    {
      mime: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
      label: 'Excel', extensions: ['.xlsx'], view: null, recognized: false,
    },
    { mime: 'application/vnd.ms-excel', label: 'Excel', extensions: ['.xls'], view: null, recognized: false },
  ],
};

describe('fileShownAs', () => {
  it('показывается то, что реестр называет показываемым', () => {
    expect(fileShownAs(info, 'application/pdf')).toBe('pdf');
    expect(fileShownAs(info, 'image/png')).toBe('image');
    expect(fileShownAs(info, 'image/webp')).toBe('image');
  });

  it('известный, но не показываемый вид только скачивается', () => {
    expect(fileShownAs(info, 'application/vnd.ms-excel')).toBe('download');
  });

  // Сторож перечня: «начинается с image/» пропустило бы документ со сценариями как картинку, а
  // «содержит pdf» — вид, которого сервер не называл.
  it('вид, которого в реестре нет, только скачивается — даже похожий на известный', () => {
    expect(fileShownAs(info, 'application/octet-stream')).toBe('download');
    expect(fileShownAs(info, 'image/svg+xml')).toBe('download');
    expect(fileShownAs(info, 'application/x-pdf-like')).toBe('download');
    expect(fileShownAs(info, 'image/tiff')).toBe('download');
    expect(fileShownAs(info, '')).toBe('download');
    expect(fileShownAs(info, null)).toBe('download');
  });

  it('без реестра показать нечем', () => {
    expect(fileShownAs(undefined, 'application/pdf')).toBe('download');
  });
});

describe('перечни из реестра', () => {
  it('выбор файла называет и типы, и расширения', () => {
    expect(acceptOf(recognizedKinds(info)))
      .toBe('application/pdf,image/png,image/jpeg,.pdf,.png,.jpg,.jpeg');
    expect(acceptOf([])).toBe('');
  });

  it('показываемое и распознаваемое — разные перечни', () => {
    expect(shownKinds(info).map(kind => kind.label)).toEqual(['PDF', 'PNG', 'JPEG', 'WebP']);
    expect(recognizedKinds(info).map(kind => kind.label)).toEqual(['PDF', 'PNG', 'JPEG']);
    expect(shownKinds(undefined)).toEqual([]);
  });

  it('слова: последний через союз, одинаковые названия — один раз', () => {
    expect(wordsOf(recognizedKinds(info))).toBe('PDF, PNG и JPEG');
    expect(wordsOf(recognizedKinds(info), 'или')).toBe('PDF, PNG или JPEG');
    expect(wordsOf(info.kinds.filter(kind => kind.view === null))).toBe('Excel');
    expect(wordsOf([])).toBe('');
  });

  it('расширение — первое у вида', () => {
    expect(fileExtension(info, 'image/jpeg')).toBe('.jpg');
    expect(fileExtension(info, 'text/html')).toBe('');
  });
});
