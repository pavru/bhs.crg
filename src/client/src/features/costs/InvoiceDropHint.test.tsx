// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { FileKind } from '@/shared/api/fileKinds';
import { InvoiceDropHint } from './InvoiceDropHint';

/** Подсказка о броске файлов (shell-07-scan-drop-hint, [COST-8] [COST-31]): правдива о месте, форматах и фоне. */

let wide = true;
beforeEach(() => {
  wide = true;
  window.matchMedia = vi.fn().mockImplementation(() => ({
    matches: wide, addEventListener: () => {}, removeEventListener: () => {},
  })) as unknown as typeof window.matchMedia;
});
afterEach(cleanup);

const kind = (label: string) => ({ label } as FileKind);
const KINDS = [kind('PDF'), kind('PNG'), kind('JPEG'), kind('Excel'), kind('Excel'), kind('Word')];
const line = () => screen.getByTestId('invoice-drop-hint');

describe('подсказка о броске файлов', () => {
  it('называет форматы реестра в скобках и всё окно целью', () => {
    render(<InvoiceDropHint kinds={KINDS} onPick={() => {}} />);
    expect(line().textContent).toBe('Перетащите файлы счетов (PDF, PNG, JPEG, Excel и Word) в любое место окна — '
      + 'заведём черновики и распознаем их в фоне, поля останется проверить.Выбрать файл');
  });

  it('реестр не пришёл — без скобок, а не выдуманный перечень', () => {
    render(<InvoiceDropHint kinds={[]} onPick={() => {}} />);
    expect(line().textContent).toContain('Перетащите файлы счетов в любое место окна');
    expect(line().textContent).not.toContain('(');
  });

  it('«Выбрать файл» открывает тот же выбор файла', () => {
    const onPick = vi.fn();
    render(<InvoiceDropHint kinds={KINDS} onPick={onPick} />);
    fireEvent.click(screen.getByRole('button', { name: 'Выбрать файл' }));
    expect(onPick).toHaveBeenCalledOnce();
  });

  it('строка — чертой сверху, без пунктира: целью служит всё окно, а не площадка', () => {
    render(<InvoiceDropHint kinds={KINDS} onPick={() => {}} />);
    expect(line().className).toContain('border-t');
    expect(line().className).not.toContain('dashed');
  });

  it('в пустом списке — та же фраза частью пустого состояния, без черты', () => {
    render(<InvoiceDropHint kinds={[kind('PDF')]} onPick={() => {}} inline />);
    expect(screen.queryByTestId('invoice-drop-hint')).toBeNull();
    const inline = screen.getByTestId('invoice-drop-hint-inline');
    expect(inline.textContent).toContain('(PDF) в любое место окна');
    expect(inline.className).not.toContain('border');
  });

  it('на телефоне строки нет: перетаскивать там нечем', () => {
    wide = false;
    render(<InvoiceDropHint kinds={KINDS} onPick={() => {}} />);
    expect(screen.queryByTestId('invoice-drop-hint')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Выбрать файл' })).toBeNull();
  });
});
