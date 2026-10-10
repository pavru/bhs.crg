import { describe, expect, it } from 'vitest';
import type { FileKindsInfo } from '@/shared/api/fileKinds';
import { scanSaveName } from './scanView';

const kinds: FileKindsInfo = {
  unknown: 'application/octet-stream',
  maxBytes: 1,
  kinds: [
    { mime: 'application/pdf', label: 'PDF', extensions: ['.pdf'], aliases: [], view: 'pdf', recognized: true },
    { mime: 'application/vnd.ms-excel', label: 'Excel', extensions: ['.xls'], aliases: [], view: null, recognized: false },
  ],
};

describe('scanSaveName', () => {
  it('имя файла берётся как есть', () => {
    expect(scanSaveName('Счёт 417.xlsx', 'application/octet-stream', kinds)).toBe('Счёт 417.xlsx');
  });

  it('файлу без имени расширение даёт вид, а неизвестному виду — ничего', () => {
    expect(scanSaveName(null, 'application/pdf', kinds)).toBe('Скан счёта.pdf');
    expect(scanSaveName('', 'application/vnd.ms-excel', kinds)).toBe('Скан счёта.xls');
    expect(scanSaveName(null, 'application/octet-stream', kinds)).toBe('Скан счёта');
  });

  it('без реестра имя остаётся без расширения', () => {
    expect(scanSaveName(null, 'application/pdf', undefined)).toBe('Скан счёта');
  });
});
