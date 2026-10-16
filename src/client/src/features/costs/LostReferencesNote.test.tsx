// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import type { InvoiceView } from '@/shared/api/invoices';
import { LostReferencesNote } from './LostReferencesNote';

/**
 * Сводка потерянных ссылок у счёта закрытого периода (E2, issue #1100; ревью PR #1392). Чем ломается:
 * человеку без права исправления называют путь, которого у него нет, — или тому, у кого он есть, говорят
 * «исправить нельзя».
 */

afterEach(cleanup);

/** Поставщик удалён из справочника — одна потерянная ссылка, остальное на месте. */
const VIEW = {
  references: { supplier: 'lost', payer: 'present', documentType: 'present' },
  lines: [],
  allocation: { document: { parts: [] } },
} as unknown as InvoiceView;

const text = (locked: Parameters<typeof LostReferencesNote>[0]['locked']) => {
  render(<LostReferencesNote view={VIEW} locked={locked} />);
  const note = screen.getByRole('note').textContent ?? '';
  cleanup();
  return note;
};

describe('сводка потерянных ссылок', () => {
  it('у счёта закрытого периода путь назван только тому, у кого есть «Исправить»', () => {
    expect(text('correct')).toContain('заменяют исправлением с причиной');
    expect(text('closed')).not.toContain('исправлением');
    expect(text('closed')).toContain('здесь это не исправить');
  });

  it('в режиме исправления и у открытого счёта — выбрать другую запись', () => {
    expect(text(null)).toContain('выбрать в поле другую запись');
    expect(text('gone')).toContain('Счёт удалён');
  });
});
