// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { renderHook } from '@testing-library/react';
import { claimWindowDrop, useDropMissGuard, type WindowDropClaim } from './fileDrop';

/**
 * Захват броска окном (costs-invoice-screen-14, D1): без захвата страж отклоняет бросок, как раньше; с ним —
 * отдаёт событие захвату. При открытом модальном слое захват молчит, покрытие гаснет на `drop` и `dragend`.
 */

const pdf = new File(['x'], 'счёт.pdf', { type: 'application/pdf' });

function drag(type: string, files: File[] = [pdf]) {
  const e = new Event(type, { bubbles: true, cancelable: true });
  const transfer = {
    types: ['Files'], dropEffect: 'move', files,
    items: files.map(f => ({ kind: 'file', getAsFile: () => f, webkitGetAsEntry: () => ({ isDirectory: false }) })),
  };
  Object.defineProperty(e, 'dataTransfer', { value: transfer });
  Object.defineProperty(e, 'relatedTarget', { value: null });
  window.dispatchEvent(e);
  return { e, transfer };
}

const claimOf = (state: 'accept' | 'busy' = 'accept'): WindowDropClaim => ({
  state: () => state, onFiles: vi.fn(), onOver: vi.fn(),
});

let release: (() => void) | null = null;
afterEach(() => { release?.(); release = null; document.body.innerHTML = ''; });

describe('страж промаха и захват окна', () => {
  it('без захвата страж отклоняет бросок, как раньше', () => {
    renderHook(() => useDropMissGuard());
    const { e, transfer } = drag('dragover');
    expect(e.defaultPrevented).toBe(true);
    expect(transfer.dropEffect).toBe('none');
  });

  it('с захватом — отдаёт ему: «копировать», покрытие, файлы по броску', () => {
    renderHook(() => useDropMissGuard());
    const claim = claimOf();
    release = claimWindowDrop(claim);
    const over = drag('dragover', [pdf, pdf]);
    expect(over.transfer.dropEffect).toBe('copy');
    expect(claim.onOver).toHaveBeenLastCalledWith({ count: 2, state: 'accept' });

    drag('drop');
    expect(claim.onFiles).toHaveBeenCalledWith([pdf], []);
    // Покрытие гаснет на броске.
    expect(claim.onOver).toHaveBeenLastCalledWith(null);
  });

  it('идёт загрузка — «нельзя», покрытие говорит «занят», бросок не принимается', () => {
    renderHook(() => useDropMissGuard());
    const claim = claimOf('busy');
    release = claimWindowDrop(claim);
    const { transfer } = drag('dragover');
    expect(transfer.dropEffect).toBe('none');
    expect(claim.onOver).toHaveBeenLastCalledWith({ count: 1, state: 'busy' });
    drag('drop');
    expect(claim.onFiles).not.toHaveBeenCalled();
  });

  it('при открытом модальном окне захват молчит; всплывающая панель — не слой', () => {
    renderHook(() => useDropMissGuard());
    const claim = claimOf();
    release = claimWindowDrop(claim);
    document.body.innerHTML = '<div data-radix-popper-content-wrapper><div role="dialog" data-state="open"></div></div>';
    expect(drag('dragover').transfer.dropEffect).toBe('copy');

    document.body.innerHTML += '<div role="dialog" data-state="open"></div>';
    // Слой смотрится на входе в элемент, а не на каждом dragover.
    drag('dragenter');
    const { transfer } = drag('dragover');
    expect(transfer.dropEffect).toBe('none');
    expect(claim.onOver).toHaveBeenLastCalledWith(null);
    drag('drop');
    expect(claim.onFiles).not.toHaveBeenCalled();
  });

  it('покрытие гаснет на dragend', () => {
    renderHook(() => useDropMissGuard());
    const claim = claimOf();
    release = claimWindowDrop(claim);
    drag('dragover');
    window.dispatchEvent(new Event('dragend'));
    expect(claim.onOver).toHaveBeenLastCalledWith(null);
  });

  it('покрытие гаснет, когда вышли из всех элементов — Esc, уход из окна в любом браузере', () => {
    renderHook(() => useDropMissGuard());
    const claim = claimOf();
    release = claimWindowDrop(claim);
    drag('dragenter');
    drag('dragenter');
    drag('dragover');
    drag('dragleave');
    expect(claim.onOver).toHaveBeenLastCalledWith({ count: 1, state: 'accept' });
    // Последний выход — точкой внутри окна (Firefox, отмена Esc): покрытие всё равно гаснет.
    drag('dragleave');
    expect(claim.onOver).toHaveBeenLastCalledWith(null);
  });

  it('снятый захват больше не берёт бросок; снимает только свой', () => {
    renderHook(() => useDropMissGuard());
    const first = claimOf();
    const off = claimWindowDrop(first);
    const second = claimOf();
    release = claimWindowDrop(second);
    off();
    drag('drop');
    expect(second.onFiles).toHaveBeenCalled();
    release();
    release = null;
    expect(drag('dragover').transfer.dropEffect).toBe('none');
  });

  it('событие, которое уже взяла цель, страж не трогает', () => {
    renderHook(() => useDropMissGuard());
    const claim = claimOf();
    release = claimWindowDrop(claim);
    const own = (e: Event) => e.preventDefault();
    window.addEventListener('drop', own, { capture: true });
    drag('drop');
    window.removeEventListener('drop', own, { capture: true });
    expect(claim.onFiles).not.toHaveBeenCalled();
  });
});
