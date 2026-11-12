import { describe, expect, it } from 'vitest';
import type { FileKind } from '@/shared/api/fileKinds';
import { emptyDropTail, selectHint } from './invoiceDropWords';

/** Слова о броске до броска (shell-07-scan-drop-hint, [COST-8] [COST-31]): правдивы о месте, форматах и праве. */

const kind = (label: string) => ({ label } as FileKind);
const KINDS = [kind('PDF'), kind('PNG'), kind('JPEG'), kind('Excel'), kind('Excel'), kind('Word')];

describe('описание «Выберите счёт»', () => {
  it('называет форматы реестра в скобках и исход', () => {
    expect(selectHint(true, KINDS)).toBe('Или перетащите сюда файлы счетов (PDF, PNG, JPEG, Excel и Word) — '
      + 'из каждого заведём черновик и распознаем его в фоне. Новый счёт можно завести и пустым.');
  });

  it('реестр не пришёл — без скобок, а не выдуманный перечень', () => {
    const text = selectHint(true, []);
    expect(text).toContain('Или перетащите сюда файлы счетов — из каждого');
    expect(text).not.toContain('(');
  });

  it('без права описания нет: ни «перетащите», ни «заведите»', () => {
    expect(selectHint(false, KINDS)).toBeUndefined();
  });
});

describe('хвост пустого списка', () => {
  const base = { canEdit: true, wide: true, queue: null, query: '' };

  it('без отбора и поиска на рабочем столе — о броске в окно', () => {
    expect('Счетов пока нет.' + emptyDropTail(base))
      .toBe('Счетов пока нет. Перетащите файлы счетов в окно или нажмите «Счёт из файла».');
  });

  it('под отбором, поиском, без права и на телефоне — ничего', () => {
    expect(emptyDropTail({ ...base, queue: 'parsing' })).toBe('');
    expect(emptyDropTail({ ...base, query: 'КабельТорг' })).toBe('');
    expect(emptyDropTail({ ...base, canEdit: false })).toBe('');
    expect(emptyDropTail({ ...base, wide: false })).toBe('');
  });

  it('пробелы в поиске — не поиск', () => {
    expect(emptyDropTail({ ...base, query: '  ' })).not.toBe('');
  });
});
