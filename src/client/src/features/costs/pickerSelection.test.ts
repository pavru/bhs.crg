// @vitest-environment jsdom
import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { HOVER_REST, stepIndex, usePickerSelection } from './pickerSelection';

describe('шаг выделения стрелкой', () => {
  it('из пустого — к первой вниз и к последней вверх', () => {
    expect(stepIndex(-1, 1, 3)).toBe(0);
    expect(stepIndex(-1, -1, 3)).toBe(2);
  });

  it('у края останавливается', () => {
    expect(stepIndex(2, 1, 3)).toBe(2);
    expect(stepIndex(0, -1, 3)).toBe(0);
    expect(stepIndex(1, 1, 3)).toBe(2);
  });

  it('списка нет — некуда', () => {
    expect(stepIndex(-1, 1, 0)).toBe(-1);
  });
});

describe('выделение в окне выбора (#1517)', () => {
  beforeEach(() => { vi.useFakeTimers(); });
  afterEach(() => { vi.useRealTimers(); });

  it('мышь, проведённая по строкам без остановки, выделяет только ту, где остановилась', () => {
    const ids = Array.from({ length: 10 }, (_, i) => `p${i}`);
    const { result } = renderHook(() => usePickerSelection(ids));
    // Десять строк за полсекунды — по 50 мс на строку.
    for (const id of ids) {
      act(() => { result.current.hover(id); });
      act(() => { vi.advanceTimersByTime(50); });
    }
    expect(result.current.active).toBeNull();
    act(() => { vi.advanceTimersByTime(HOVER_REST); });
    expect(result.current.active).toBe('p9');
  });

  it('ушла мышь — недостоявшееся не выделяется, выделенное остаётся', () => {
    const { result } = renderHook(() => usePickerSelection(['a', 'b']));
    act(() => { result.current.hover('a'); });
    act(() => { vi.advanceTimersByTime(HOVER_REST); });
    act(() => { result.current.hover('b'); });
    act(() => { result.current.leave(); });
    act(() => { vi.advanceTimersByTime(HOVER_REST * 2); });
    expect(result.current.active).toBe('a');
  });

  it('стрелка выделяет сразу и гасит недостоявшееся наведение', () => {
    const { result } = renderHook(() => usePickerSelection(['a', 'b', 'c']));
    act(() => { result.current.hover('c'); });
    act(() => { result.current.step(1); });
    expect(result.current.active).toBe('a');
    act(() => { vi.advanceTimersByTime(HOVER_REST); });
    expect(result.current.active).toBe('a');
    act(() => { result.current.step(1); });
    expect(result.current.index).toBe(1);
  });

  it('выделенной нет среди новых находок — выделение держится, но не в списке', () => {
    let ids = ['a', 'b'];
    const { result, rerender } = renderHook(() => usePickerSelection(ids));
    act(() => { result.current.select('b'); });
    ids = ['c'];
    rerender();
    expect(result.current.active).toBe('b');
    expect(result.current.index).toBe(-1);
    act(() => { result.current.step(1); });
    expect(result.current.active).toBe('c');
  });
});
