import { createContext, useContext, useRef, useEffect, useLayoutEffect, useState, useMemo } from 'react';

// Отдельным файлом от компонента (issue #858): модуль, экспортирующий и компонент, и
// функцию, не может быть границей горячей подмены — правка поднимается вверх по импортам.

// ─── Реестр редакторов (явное сохранение, issue #197 / #210) ─────────────────────
// Общий для страниц-редакторов типа list-detail (Типы документов, Типы полей). Дочерние формы
// публикуют своё состояние dirty и функцию сохранения; страница агрегирует их в одну кнопку
// «Сохранить» в шапке, бейдж «есть изменения» и диалог-гард при уходе. Сохранение может бросить —
// тогда переход не выполняется. Полноценный ListDetailShell извлечём позже (после 3-й страницы).
export interface TypeEditorRegistry {
  publish: (key: string, dirty: boolean, save: () => Promise<void>, reset?: () => void) => void;
  unpublish: (key: string) => void;
}
const TypeEditorContext = createContext<TypeEditorRegistry | null>(null);
export const TypeEditorProvider = TypeEditorContext.Provider;

/** Публикует dirty/save/reset текущей формы в реестр страницы (save/reset всегда берутся свежими через
 *  ref). `reset` откатывает локальное состояние формы к сохранённому — для кнопки «Отмена» (issue #210). */
export function useRegisterEditor(key: string, dirty: boolean, save: () => Promise<void>, reset?: () => void) {
  const ctx = useContext(TypeEditorContext);
  const saveRef = useRef(save);
  const resetRef = useRef(reset);
  /**
   * Свежий колбэк для отложенного вызова. Обновляется ЭФФЕКТОМ, а не присваиванием в рендере
   * (issue #858): рендер обязан быть чистым, а запись в ref — побочное действие, которое к тому же
   * случалось бы и на брошенном рендере (StrictMode, прерванный конкурентный проход).
   *
   * Эффект именно СЛОЙНЫЙ (useLayoutEffect). Обычный отложен до после отрисовки, и между коммитом и
   * его срабатыванием ref держит ПРЕДЫДУЩИЙ колбэк — с прежним состоянием формы. Нажатие
   * «Сохранить» в шапке, попавшее в эту щель, отправило бы прежние значения. Слойный эффект
   * выполняется в том же задании, что и коммит: события в промежуток не попадают, щели нет.
   */
  useLayoutEffect(() => {
    saveRef.current = save;
    resetRef.current = reset;
  });
  useEffect(() => {
    ctx?.publish(key, dirty, () => saveRef.current(), () => resetRef.current?.());
  }, [ctx, key, dirty]);
  useEffect(() => () => ctx?.unpublish(key), [ctx, key]);
}

/** Агрегатор реестра для корня list-detail страницы: dirty-состояние + saveAll + resetAll + provider value. */
export function useTypeEditorRegistry() {
  const [dirtyMap, setDirtyMap] = useState<Record<string, boolean>>({});
  const saversRef = useRef<Record<string, () => Promise<void>>>({});
  const resettersRef = useRef<Record<string, () => void>>({});
  const registry = useMemo<TypeEditorRegistry>(() => ({
    publish: (key, dirty, save, reset) => {
      saversRef.current[key] = save;
      if (reset) resettersRef.current[key] = reset;
      setDirtyMap(m => m[key] === dirty ? m : { ...m, [key]: dirty });
    },
    unpublish: (key) => {
      delete saversRef.current[key];
      delete resettersRef.current[key];
      setDirtyMap(m => (key in m ? (() => { const n = { ...m }; delete n[key]; return n; })() : m));
    },
  }), []);
  const anyDirty = Object.values(dirtyMap).some(Boolean);
  const [saving, setSaving] = useState(false);
  const saveAll = async () => {
    setSaving(true);
    try { for (const s of Object.values(saversRef.current)) await s(); }
    finally { setSaving(false); }
  };
  const resetAll = () => { for (const r of Object.values(resettersRef.current)) r(); };
  return { registry, anyDirty, saving, saveAll, resetAll };
}
