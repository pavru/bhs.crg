import { describe, expect, it } from 'vitest';
import type { Template } from '@/shared/api/types';
import { newVersionPlan, nextVersionOf } from './groupTemplates';

const t = (name: string, version: number, isActive = false, isDefault = false) =>
  ({ id: `${name}-${version}`, name, version, isActive, isDefault }) as Template;

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

describe('newVersionPlan (issue #1920)', () => {
  const list = [t('Основной', 2), t('Основной', 4, true, true), t('Сокращённый', 1, true)];

  it('от исторической — гаснет действующая того же шаблона, «по умолчанию» переходит, закреплённые считаются', () => {
    const plan = newVersionPlan(list, list[0], { 'Основной-4': { count: 3 }, 'Сокращённый-1': { count: 9 } });
    expect(plan).toEqual({ next: 5, retires: [list[1]], takesDefault: true, pinnedToRetired: 3, noPinDocsMove: true });
  });

  it('от действующей — гаснуть больше нечему', () => {
    expect(newVersionPlan(list, list[1])).toEqual({ next: 5, retires: [], takesDefault: false, pinnedToRetired: 0, noPinDocsMove: true });
  });

  it('от действующей — закреплённые за ней считаются: сервер пересадит их на новую (#1923)', () => {
    expect(newVersionPlan(list, list[1], { 'Основной-4': { count: 3 }, 'Основной-2': { count: 7 } }).pinnedToRetired).toBe(3);
  });

  it('по умолчанию у типа — другой шаблон: документы без выбора остаются на нём', () => {
    expect(newVersionPlan(list, list[2]).noPinDocsMove).toBe(false);
  });

  it('у типа нет шаблона по умолчанию: документы без выбора берут любой действующий и переходят', () => {
    const plain = [t('А', 1), t('А', 2, true), t('Б', 1, true)];
    expect(newVersionPlan(plain, plain[0]).noPinDocsMove).toBe(true);
  });
});
