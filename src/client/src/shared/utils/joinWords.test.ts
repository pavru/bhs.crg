import { describe, expect, it } from 'vitest';
import { joinWords } from './joinWords';

describe('перечень словами', () => {
  it('ни одного, один, два, три — и союз на выбор', () => {
    expect(joinWords([])).toBe('');
    expect(joinWords(['2611'])).toBe('2611');
    expect(joinWords(['2611', '2612'])).toBe('2611 и 2612');
    expect(joinWords(['PDF', 'PNG', 'JPEG'], 'или')).toBe('PDF, PNG или JPEG');
  });
});
