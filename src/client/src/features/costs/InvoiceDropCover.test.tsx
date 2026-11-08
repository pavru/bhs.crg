// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { DropCover } from './InvoiceDropCover';
import type { ScanBatch } from './scanBatch';

/** Покрытие броска (costs-invoice-screen-14, D1, D5): говорит, что будет с брошенным, и где искать ход. */

afterEach(cleanup);
const text = () => screen.getByTestId('invoice-drop-cover').textContent ?? '';

describe('покрытие броска', () => {
  it('один файл на открытый счёт с файлом: новый черновик, счёт не изменится, замена — в «⋮»', () => {
    render(<DropCover over={{ count: 1, state: 'accept' }} opened={{ number: '104', hasFile: true }} batch={null} checking={false} listShown />);
    expect(text()).toContain('Отпустите — заведём черновик из файла');
    expect(text()).toContain('Откроем новый черновик; счёт 104 не изменится.');
    expect(text()).toContain('Заменить файл этого счёта — «⋮ → Заменить файл…»');
  });

  it('пакет без списка — ход в центре активности', () => {
    render(<DropCover over={{ count: 3, state: 'accept' }} opened={{ number: '104' }} batch={null} checking={false} listShown={false} />);
    expect(text()).toContain('Отпустите — заведём 3 черновика, по одному на файл');
    expect(text()).toContain('ход — в центре активности');
  });

  it('занят — «Идёт загрузка N из M — бросок примем после неё»', () => {
    const batch = { phase: 'running', settled: 3, total: 10 } as ScanBatch;
    render(<DropCover over={{ count: 1, state: 'busy' }} opened={null} batch={batch} checking={false} listShown />);
    expect(text()).toBe('Идёт загрузка 4 из 10 — бросок примем после неё');
  });
});
