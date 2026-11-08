import { describe, expect, it } from 'vitest';
import type { FileKind, FileKindsInfo } from '@/shared/api/fileKinds';
import { attachKinds, precheck } from './scanPrecheck';

/** Предпроверка (#1265): заведение — распознаваемые виды; приложение к счёту — ещё и показываемые (ревью PR #1823). */

const kind = (mime: string, label: string, over: Partial<FileKind> = {}): FileKind =>
  ({ mime, label, extensions: [], aliases: [], view: 'image', recognized: false, ...over });
const kinds: FileKindsInfo = {
  unknown: 'application/octet-stream', maxBytes: 50 * 1024 * 1024,
  kinds: [
    kind('application/pdf', 'PDF', { view: 'pdf', recognized: true }),
    kind('image/webp', 'WebP'),
    kind('application/vnd.ms-excel', 'Excel', { view: null, recognized: true }),
    kind('application/zip', 'ZIP', { view: null }),
  ],
};
const file = (type: string) => new File(['x'], 'файл', { type });

describe('предпроверка файла', () => {
  it('заведение — только распознаваемое: WebP не годится', () => {
    expect(precheck(file('application/pdf'), kinds)).toBeNull();
    expect(precheck(file('image/webp'), kinds)).toBe('не PDF или Excel');
  });

  it('приложение — показываемое или распознаваемое: WebP годится, ZIP — нет', () => {
    const offered = attachKinds(kinds);
    expect(offered.map(k => k.label)).toEqual(['PDF', 'WebP', 'Excel']);
    expect(precheck(file('image/webp'), kinds, offered)).toBeNull();
    expect(precheck(file('application/zip'), kinds, offered)).toBe('не PDF, WebP или Excel');
  });
});
