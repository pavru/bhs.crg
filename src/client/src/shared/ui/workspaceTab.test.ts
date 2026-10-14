// @vitest-environment jsdom
import { describe, it, expect } from 'vitest';
import { forgetTabWorkspaces, readTab, tabKey, writeTab } from './workspaceTab';

/**
 * Вкладка помнит выбор пространства только до конца сессии (ревью PR #1330): повторный вход в той же
 * вкладке обязан открыть умолчание из профиля и заново сказать об откате.
 */
describe('выбор пространства во вкладке', () => {
  it('выход стирает выбор и признак показанного сообщения — и только их', () => {
    const key = tabKey('a@b.c');
    writeTab(key, 'all');
    writeTab(`${key}:notice`, 'Пространство «Х» осталось без доступных модулей');
    sessionStorage.setItem('чужое', '1');

    forgetTabWorkspaces();

    expect(readTab(key)).toBeNull();
    expect(readTab(`${key}:notice`)).toBeNull();
    expect(sessionStorage.getItem('чужое')).toBe('1');
  });
});
