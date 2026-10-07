import { describe, expect, it } from 'vitest';
import type { ModuleSetting } from '@/shared/api/moduleSettings';
import {
  boundsHint, changedValues, changeWarnings, fieldErrors, fieldText, localRefusals, sentence, shown, staleStored,
  toServer,
} from './moduleSettings';

function tolerance(over: Partial<ModuleSetting> = {}): ModuleSetting {
  return {
    key: 'costs.allocation.tolerance', title: 'Допуск расхождения сумм', effect: 'Что меняет',
    kind: 'number', value: '1.00', stored: null, default: '1.00',
    min: 0, max: 100, scale: 2, unit: '₽', changeWarning: 'Действует на все счета.', ...over,
  };
}

const KEY = 'costs.allocation.tolerance';

describe('fieldText — что стоит в поле', () => {
  it('нетронутое поле показывает действующее значение с запятой', () => {
    expect(fieldText(tolerance({ value: '0.50', stored: '0.50' }), {})).toBe('0,50');
  });

  it('набранное показывается как набрано — без переформатирования под рукой', () => {
    expect(fieldText(tolerance(), { [KEY]: '0,' })).toBe('0,');
  });

  it('после «вернуть умолчание» в поле умолчание', () => {
    expect(fieldText(tolerance({ value: '5.00', stored: '5.00' }), { [KEY]: null })).toBe('1,00');
  });
});

describe('changedValues — что уйдёт на сервер', () => {
  it('нетронутая форма не шлёт ничего', () => {
    expect(changedValues([tolerance()], {})).toEqual({});
  });

  it('набранное уходит с точкой и без пробелов', () => {
    expect(changedValues([tolerance()], { [KEY]: ' 0,5 ' })).toEqual({ [KEY]: '0.5' });
  });

  it('то же число другой записью — не изменение', () => {
    expect(changedValues([tolerance()], { [KEY]: '1' })).toEqual({});
    expect(changedValues([tolerance()], { [KEY]: '1,0' })).toEqual({});
    expect(changedValues([tolerance({ value: '5.00', stored: '5.00' })], { [KEY]: '5' })).toEqual({});
  });

  it('негодное уходит как есть — причину назовёт сервер, а не догадка формы', () => {
    expect(changedValues([tolerance()], { [KEY]: 'рубль' })).toEqual({ [KEY]: 'рубль' });
    expect(changedValues([tolerance()], { [KEY]: '' })).toEqual({ [KEY]: '' });
  });

  it('сброс уходит, только когда есть что сбрасывать', () => {
    expect(changedValues([tolerance({ value: '5.00', stored: '5.00' })], { [KEY]: null })).toEqual({ [KEY]: null });
    expect(changedValues([tolerance()], { [KEY]: null })).toEqual({});
  });

  it('негодное сохранённое можно перезаписать тем же числом, что действует', () => {
    const stale = tolerance({ value: '1.00', stored: '500' });
    expect(changedValues([stale], { [KEY]: '1' })).toEqual({ [KEY]: '1' });
  });
});

describe('localRefusals — что видно без сервера', () => {
  it('не число и пустое', () => {
    expect(localRefusals([tolerance()], { [KEY]: 'рубль' })).toEqual({ [KEY]: 'нужно число' });
    expect(localRefusals([tolerance()], { [KEY]: '' })).toEqual({ [KEY]: 'нужно число' });
    expect(localRefusals([tolerance()], { [KEY]: '1 000' })).toEqual({ [KEY]: 'нужно число' });
    expect(localRefusals([tolerance()], { [KEY]: '1e2' })).toEqual({ [KEY]: 'нужно число' });
  });

  it('вне границ — названы границы и единица', () => {
    expect(localRefusals([tolerance()], { [KEY]: '500' })).toEqual({ [KEY]: 'допустимо от 0 до 100 ₽' });
    expect(localRefusals([tolerance()], { [KEY]: '-1' })).toEqual({ [KEY]: 'допустимо от 0 до 100 ₽' });
  });

  it('годное, сброс и неизменённое не отвергаются', () => {
    expect(localRefusals([tolerance()], { [KEY]: '0.5' })).toEqual({});
    expect(localRefusals([tolerance()], { [KEY]: '100' })).toEqual({});
    expect(localRefusals([tolerance()], { [KEY]: null })).toEqual({});
    expect(localRefusals([tolerance()], {})).toEqual({});
  });

  it('лишние знаки после запятой оставлены серверу', () => {
    expect(localRefusals([tolerance()], { [KEY]: '0.005' })).toEqual({});
  });
});

describe('changeWarnings — предупреждение до сохранения', () => {
  it('называет настройку, прежнее и новое значение', () => {
    expect(changeWarnings([tolerance()], { [KEY]: '0.10' })).toEqual([{
      key: KEY, title: 'Допуск расхождения сумм', warning: 'Действует на все счета.',
      from: '1,00 ₽', to: '0,10 ₽',
    }]);
  });

  it('при сбросе новое значение — умолчание', () => {
    const [warning] = changeWarnings([tolerance({ value: '5.00', stored: '5.00' })], { [KEY]: null });
    expect(warning.from).toBe('5,00 ₽');
    expect(warning.to).toBe('1,00 ₽');
  });

  it('у настройки без предупреждения и у неизменённой его нет', () => {
    expect(changeWarnings([tolerance({ changeWarning: null })], { [KEY]: '0.10' })).toEqual([]);
    expect(changeWarnings([tolerance()], {})).toEqual([]);
  });
});

describe('подписи', () => {
  it('подсказка называет умолчание и границы', () => {
    expect(boundsHint(tolerance())).toBe('По умолчанию: 1,00 ₽ · допустимо от 0 до 100');
    expect(boundsHint(tolerance({ min: null, max: null, unit: null }))).toBe('По умолчанию: 1,00');
  });

  it('сохранено одно, действует другое — сказано словами', () => {
    expect(staleStored(tolerance())).toBeNull();
    expect(staleStored(tolerance({ value: '5.00', stored: '5.00' }))).toBeNull();
    expect(staleStored(tolerance({ value: '1.00', stored: '500' }))).toContain('Сохранено «500»');
  });

  it('число показывается с запятой, уходит с точкой', () => {
    expect(shown('0.50')).toBe('0,50');
    expect(toServer('0,50')).toBe('0.50');
  });

  it('причина сервера начинается с заглавной', () => {
    expect(sentence('допустимо от 0.00 до 100.00')).toBe('Допустимо от 0.00 до 100.00');
    expect(sentence('')).toBe('');
  });
});

describe('fieldErrors — отказ у своего поля', () => {
  it('берёт причины по ключам из ответа', () => {
    const e = { response: { data: { error: 'Настройки не сохранены', fields: { [KEY]: 'нужно число' } } } };
    expect(fieldErrors(e)).toEqual({ [KEY]: 'нужно число' });
  });

  it('отказ не про поле и сетевой сбой — пусто', () => {
    expect(fieldErrors({ response: { data: { error: 'Модуль не включён' } } })).toEqual({});
    expect(fieldErrors(new Error('Network Error'))).toEqual({});
    expect(fieldErrors({ response: { data: { fields: 'строка' } } })).toEqual({});
  });
});
