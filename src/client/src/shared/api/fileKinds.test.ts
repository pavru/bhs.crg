import { describe, expect, it } from 'vitest';
import {
  acceptOf, fileExtension, fileShownAs, kindNamed, recognizedKinds, shownKinds, sizeLimitWords, wordsOf,
  type FileKindsInfo,
} from './fileKinds';

/** Реестр в том виде, в каком его отдаёт сервер. */
const info: FileKindsInfo = {
  unknown: 'application/octet-stream',
  maxBytes: 50 * 1024 * 1024,
  kinds: [
    { mime: 'application/pdf', label: 'PDF', extensions: ['.pdf'], aliases: ['application/x-pdf'], view: 'pdf', recognized: true },
    { mime: 'image/png', label: 'PNG', extensions: ['.png'], aliases: [], view: 'image', recognized: true },
    { mime: 'image/jpeg', label: 'JPEG', extensions: ['.jpg', '.jpeg'], aliases: ['image/jpg', 'image/pjpeg'], view: 'image', recognized: true },
    { mime: 'image/webp', label: 'WebP', extensions: ['.webp'], aliases: [], view: 'image', recognized: false },
    {
      mime: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
      label: 'Excel', extensions: ['.xlsx'], aliases: [], view: null, recognized: false,
    },
    { mime: 'application/vnd.ms-excel', label: 'Excel', extensions: ['.xls'], aliases: [], view: null, recognized: false },
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

describe('название браузера', () => {
  it('вид находится и по основному имени, и по синониму', () => {
    expect(kindNamed(info, 'image/pjpeg')?.label).toBe('JPEG');
    expect(kindNamed(info, ' Application/PDF ')?.label).toBe('PDF');
    expect(kindNamed(info, 'image/tiff')).toBeUndefined();
    expect(kindNamed(info, '')).toBeUndefined();
    expect(kindNamed(undefined, 'application/pdf')).toBeUndefined();
  });

  // Синоним — только для названия браузера: тип, с которым файл ОТДАЛ сервер, сверяется точно.
  it('показ по синониму не решается', () => {
    expect(fileShownAs(info, 'application/x-pdf')).toBe('download');
  });

  it('предел размера словами', () => {
    expect(sizeLimitWords(info)).toBe('50 МБ');
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
