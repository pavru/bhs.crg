import { useSyncedPreference } from './useSyncedPreference';

/**
 * Блок «Полезно знать» на Главной показан или скрыт (shell-08, ТЗ UI-37) — ключ `home.tips` учётной
 * записи. «Скрыть» в блоке пишет `hidden`, переключатель профиля возвращает `shown`: без него «насовсем»
 * было бы без выхода. Ключа нет — показан.
 */
export function useHomeTips() {
  const [value, set, save] = useSyncedPreference('home.tips', 'crg-home-tips', 'shown');
  return { shown: value !== 'hidden', setShown: (on: boolean) => set(on ? 'shown' : 'hidden'), save };
}
