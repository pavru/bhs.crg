import { describe, it, expect } from 'vitest';
import { initialsOf } from './Avatar';

describe('initialsOf', () => {
  it('две буквы из двух слов имени', () => {
    expect(initialsOf('Иван Петров')).toBe('ИП');
    expect(initialsOf('john doe')).toBe('JD');
  });

  it('одно слово — первые две буквы', () => {
    expect(initialsOf('Админ')).toBe('АД');
  });

  it('падает на email, если имени нет', () => {
    expect(initialsOf('', 'alex@bhs.local')).toBe('AL');
    expect(initialsOf(null, 'bob@x.com')).toBe('BO');
  });

  it('пусто → пустая строка', () => {
    expect(initialsOf()).toBe('');
    expect(initialsOf('   ')).toBe('');
  });
});
