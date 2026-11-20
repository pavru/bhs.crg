type Handle = ReturnType<typeof setTimeout>;

/** Таймеры уровня страницы — см. `pageTimers`. */
export interface PageTimers {
  set(fn: () => void, ms?: number): Handle;
  /** Снять таймер; `undefined` — ничего не делать. */
  clear(handle: Handle | undefined): void;
  /** Снять все, что не сработали, — сброс модульного состояния. */
  clearAll(): void;
}

/**
 * Таймеры уровня страницы: модульное состояние, которому НАРОЧНО положено пережить компонент, — слои
 * истории ждут шага, который может не прийти; кольцо на выходе окна гаснет само; ссылка на файл
 * отзывается через минуту, когда вкладка давно открыта. Таймер компонента сюда не ставят: его снимает
 * размонтирование — `useTimeouts` из `shared/hooks`.
 *
 * <p>Отдельным помощником, а не голым `setTimeout`, — чтобы намерение было видно: сторож таймеров в тестах
 * (`shared/testing/timerLeaks.ts`) такие таймеры утечкой не считает и снимает сам в конце файла, а голый
 * таймер, переживший тест, называет. И чтобы сброс модуля (`resetHistoryLayers`) мог снять свои таймеры
 * разом: иначе таймер прошлого теста менял бы состояние следующего.</p>
 */
export function pageTimers(): PageTimers {
  const live = new Set<Handle>();
  return {
    set(fn, ms) {
      const handle = setTimeout(() => { live.delete(handle); fn(); }, ms);
      live.add(handle);
      return handle;
    },
    clear(handle) {
      if (handle === undefined) return;
      live.delete(handle);
      clearTimeout(handle);
    },
    clearAll() {
      for (const handle of live) clearTimeout(handle);
      live.clear();
    },
  };
}
