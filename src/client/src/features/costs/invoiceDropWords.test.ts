import { describe, expect, it } from 'vitest';
import type { FileKind, FileKindsInfo } from '@/shared/api/fileKinds';
import { dropInvited, fromFileTitle, selectHint } from './invoiceDropWords';
import { emptyText } from './invoiceQueues';

/** Слова о броске до броска (shell-07-scan-drop-hint, [COST-8] [COST-31]): правдивы о месте, форматах и праве. */

const kind = (label: string, recognized = true) => ({ label, recognized } as FileKind);
const KINDS = [kind('PDF'), kind('PNG'), kind('JPEG'), kind('Excel'), kind('Excel'), kind('Word')];
const info = (kinds: FileKind[]) => ({ kinds } as FileKindsInfo);

describe('звать ли к броску', () => {
  const base = { canEdit: true, coarse: false, info: info(KINDS) };

  it('право, мышь и распознаваемые виды — зовём', () => {
    expect(dropInvited(base)).toBe(true);
  });

  it('реестр ещё не пришёл — зовём: бросок примут, перечень допишется', () => {
    expect(dropInvited({ ...base, info: undefined })).toBe(true);
  });

  it('без права, на касании (планшет любой ширины) и когда не распознаётся ничего — не зовём', () => {
    expect(dropInvited({ ...base, canEdit: false })).toBe(false);
    expect(dropInvited({ ...base, coarse: true })).toBe(false);
    expect(dropInvited({ ...base, info: info([kind('PDF', false), kind('Word', false)]) })).toBe(false);
  });
});

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

  it('не зовём — описания нет: ни «перетащите», ни «заведите»', () => {
    expect(selectHint(false, KINDS)).toBeUndefined();
  });
});

describe('подсказка «Счёт из файла» — тот же перечень', () => {
  it('с реестром — через двоеточие, без него — без перечня', () => {
    expect(fromFileTitle(KINDS)).toBe('Один или несколько файлов: PDF, PNG, JPEG, Excel и Word. Файлы можно перетащить в окно.');
    expect(fromFileTitle([])).toBe('Один или несколько файлов. Файлы можно перетащить в окно.');
  });
});

describe('пустой список', () => {
  it('без отбора и поиска, когда зовём, — о броске в окно', () => {
    expect(emptyText(null, '', undefined, true))
      .toBe('Счетов пока нет. Перетащите файлы счетов в окно или нажмите «Счёт из файла».');
    expect(emptyText(null, '  ', undefined, true)).toContain('Перетащите');
  });

  it('не зовём, под отбором или поиском — без слов о броске', () => {
    expect(emptyText(null, '', undefined)).toBe('Счетов пока нет.');
    expect(emptyText('parsing', '', undefined, true)).not.toContain('Перетащите');
    expect(emptyText(null, 'КабельТорг', undefined, true)).toBe('Ничего не найдено.');
  });
});
