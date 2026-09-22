import { describe, it, expect } from 'vitest';
import type { AccessInfo } from './access';
import { CORE_OWNER, isOfferedType, offeredTypes, ownerOptions, ownerTitle } from './typeOwners';

const access: AccessInfo = {
  permissions: [],
  modules: [{ code: 'id', title: 'Исполнительная документация', available: true }],
};

const type = (module: string) => ({ module });

describe('владелец типа', () => {
  it('в список владельцев входит ядро и включённые модули', () => {
    expect(ownerOptions(access).map(o => o.code)).toEqual([CORE_OWNER, 'id']);
  });

  it('владельца называет словами, а не кодом', () => {
    expect(ownerTitle('id', access)).toBe('Исполнительная документация');
    expect(ownerTitle(CORE_OWNER, access)).toBe('Ядро');
  });

  it('незнакомого владельца показывает кодом, а не прячет', () => {
    // Так выглядит тип модуля, который на этом экземпляре выключен. Пустое место в строке
    // «Владелец» читалось бы как «ничей», а это разные вещи.
    expect(ownerTitle('work', access)).toBe('work');
  });
});

describe('что предлагать к выбору', () => {
  it('тип ядра предлагается всегда', () => {
    expect(isOfferedType(type(CORE_OWNER), access)).toBe(true);
  });

  it('тип включённого модуля предлагается', () => {
    expect(isOfferedType(type('id'), access)).toBe(true);
  });

  it('тип выключенного модуля не предлагается', () => {
    expect(isOfferedType(type('work'), access)).toBe(false);
  });

  it('пока ответ о доступе НЕ ПРИШЁЛ, не прячется ничего', () => {
    // Цена обратного решения измерена живым прогоном: подставив сюда пустой доступ, первая
    // редакция на первом кадре оставляла список без единого типа модуля — страница выбирала
    // «первый в списке» из остатка и открывала не тот тип. «Ещё не знаем» и «модулей нет» — разные
    // ответы, и различать их обязана именно эта функция.
    expect(isOfferedType(type('id'), undefined)).toBe(true);
    expect(isOfferedType(type('work'), undefined)).toBe(true);
  });

  it('а пустой ПОЛУЧЕННЫЙ доступ прячет типы модулей', () => {
    const none: AccessInfo = { permissions: [], modules: [] };
    expect(isOfferedType(type('id'), none)).toBe(false);
    expect(isOfferedType(type(CORE_OWNER), none)).toBe(true);
  });

  it('список фильтруется целиком', () => {
    const types = [type(CORE_OWNER), type('id'), type('work')];
    expect(offeredTypes(types, access)).toHaveLength(2);
  });
});
