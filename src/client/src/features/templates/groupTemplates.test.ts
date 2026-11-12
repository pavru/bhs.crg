import { describe, expect, it } from 'vitest';
import type { Template } from '@/shared/api/types';
import { nextVersionOf } from './groupTemplates';

const t = (name: string, version: number, isActive = false) =>
  ({ id: `${name}-${version}`, name, version, isActive }) as Template;

describe('nextVersionOf (issue #1920)', () => {
  const list = [t('Основной', 1), t('Основной', 2), t('Основной', 3), t('Основной', 4, true), t('Сокращённый', 7, true)];

  it('от исторической версии — наибольший номер шаблона плюс один, а не «её номер плюс один»', () => {
    expect(nextVersionOf(list, list[1])).toBe(5);
  });

  it('версии другого шаблона того же типа не считаются', () => {
    expect(nextVersionOf(list, list[3])).toBe(5);
    expect(nextVersionOf(list, list[4])).toBe(8);
  });

  it('список ещё не пришёл — номер от самой версии', () => {
    expect(nextVersionOf([], list[1])).toBe(3);
  });
});
