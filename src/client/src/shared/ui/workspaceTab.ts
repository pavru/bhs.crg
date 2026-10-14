/**
 * Выбор рабочего пространства, который помнит ВКЛАДКА (issue #1001): перезагрузка страницы — не
 * вход, и откатывать человека на умолчание из профиля посреди работы было бы странно.
 *
 * <p>⚠️ Вкладка помнит его только в пределах сессии: выход стирает и выбор, и признак показанного
 * сообщения об откате (ревью PR #1330). Иначе повторный вход в той же вкладке не был бы входом —
 * умолчание из профиля не открылось бы, а сообщение «пространство осталось без модулей» не пришло
 * бы, потому что «уже показано».</p>
 */
const PREFIX = 'crg.workspace:';

export function tabKey(email: string): string {
  return `${PREFIX}${email}`;
}

export function readTab(key: string | null): string | null {
  if (!key) return null;
  try { return sessionStorage.getItem(key); } catch { return null; }
}

export function writeTab(key: string | null, value: string) {
  if (!key) return;
  try { sessionStorage.setItem(key, value); } catch { /* без хранилища — до перезагрузки */ }
}

/** Конец сессии: всё, что вкладка помнила о пространствах, — прочь. */
export function forgetTabWorkspaces() {
  try {
    for (const key of Object.keys(sessionStorage)) if (key.startsWith(PREFIX)) sessionStorage.removeItem(key);
  } catch { /* хранилища нет — и помнить было нечего */ }
}
