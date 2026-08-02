import { describe, it, expect } from 'vitest';
import { identityKey, isIdentityKeyEmpty, normalizeKey } from './identityKey';

/**
 * Составной ключ идентичности (issue #582). Клиент заводит связку «материал → документ качества», а
 * находит её сервер, поэтому здесь проверяются ровно те же свойства, что в серверных
 * IdentityKeyTests: расхождение означает связку, которая заведена и никогда не срабатывает.
 */
describe('нормализация значения', () => {
  it('схлопывает пробелы, срезает хвостовые точки, приводит регистр', () => {
    expect(normalizeKey(' Провод  ВВГ 3х2.5 ')).toBe('провод ввг 3х2.5');
    expect(normalizeKey('Шт.')).toBe('шт');
  });

  it('легаси-маркер ссылки к значению не относится', () => {
    expect(normalizeKey('🔗 Трубка')).toBe('трубка');
  });

  it('пусто на входе — пусто на выходе', () => {
    expect(normalizeKey(null)).toBe('');
    expect(normalizeKey('   ')).toBe('');
  });
});

describe('составной ключ', () => {
  it('склеивает все значения нормализованными', () => {
    expect(identityKey([' Провод  ВВГ 3х2.5 ', 'AB-12'])).toBe('провод ввг 3х2.5 | ab-12');
  });

  // Пустое поле даёт пустой СЛОТ: позиция компонента обязана быть постоянной, иначе материал без
  // артикула и материал без наименования дали бы неразличимые ключи из одного значения.
  it('пустое значение сохраняет свою позицию', () => {
    expect(identityKey(['Трубка', null])).toBe('трубка | ');
    expect(identityKey([null, 'Трубка'])).toBe(' | трубка');
    expect(identityKey(['Трубка', null])).not.toBe(identityKey([null, 'Трубка']));
  });

  it('различие в любом компоненте даёт другой ключ', () => {
    expect(identityKey(['Трубка', 'T-1'])).not.toBe(identityKey(['Трубка', 'T-2']));
  });

  it('пустым считается ключ, в котором сопоставлять нечего', () => {
    expect(isIdentityKeyEmpty(identityKey([null, '', '   ']))).toBe(true);
    expect(isIdentityKeyEmpty(identityKey([null, 'Трубка']))).toBe(false);
  });
});
