import { useCallback, useEffect, useState } from 'react';
import { useAuth } from '@/shared/hooks/useAuth';
import { resolvePreference, useSaveUserSettings, useUserSettings } from '@/shared/api/userSettings';

/**
 * Настройка, которая живёт на сервере, а в браузере держит только ЗЕРКАЛО (issue #953).
 *
 * Зеркало нужно ровно для одного: первый кадр. Тема применяется до первой отрисовки
 * (`public/theme-init.js` читает `crg-theme`), а ответ сервера приходит позже — без зеркала
 * страница успевала бы мигнуть светлой у того, кто выбрал тёмную.
 *
 * ⚠️ Правило, ради которого хук и написан: **загрузка НИЧЕГО не отправляет на сервер.** Отправка
 * бывает только по действию человека. Обратное — «увидели расхождение, подтянем локальное
 * наверх» — тихо присвоило бы новому пользователю настройки предыдущего хозяина машины, причём
 * один раз и навсегда: он бы их не выбирал и не заметил бы, откуда они взялись.
 */
export function useSyncedPreference(
  key: string,
  mirrorKey: string,
  fallback: string,
): [string, (value: string) => void] {
  const { user } = useAuth();
  const { data: server } = useUserSettings(!!user);
  const { mutate } = useSaveUserSettings();

  /**
   * Выбор, сделанный ПРЯМО СЕЙЧАС и ещё не подтверждённый ответом сервера. Нужен затем, чтобы
   * тема менялась по нажатию, а не по возвращении запроса: круг до сервера человек воспринял бы
   * как «кнопка не сработала» и нажал бы ещё раз.
   *
   * Снимается, как только ответ пришёл: дальше значение берётся у сервера — и после отказа тоже,
   * то есть несохранённый выбор откатывается на глазах, а не остаётся расхождением.
   */
  const [chosen, setChosen] = useState<string | null>(null);

  /** Зеркало читается ОДИН раз, при первом рендере: дальше оно ведомое, а не источник. */
  const [mirror] = useState(() => readMirror(mirrorKey));

  const value = chosen ?? resolvePreference(server, key, mirror, fallback);

  useEffect(() => { writeMirror(mirrorKey, value); }, [mirrorKey, value]);

  const choose = useCallback((next: string) => {
    setChosen(next);
    // Отправка — только здесь, и только потому, что это нажал человек (см. предупреждение выше).
    if (user) mutate({ [key]: next }, { onSettled: () => setChosen(null) });
  }, [key, mutate, user]);

  return [value, choose];
}

/** Приватный режим и запрет хранилища — не отказ приложения: настройка просто не запомнится. */
function readMirror(key: string): string | null {
  try { return localStorage.getItem(key); } catch { return null; }
}

function writeMirror(key: string, value: string): void {
  try { localStorage.setItem(key, value); } catch { /* память необязательна */ }
}
