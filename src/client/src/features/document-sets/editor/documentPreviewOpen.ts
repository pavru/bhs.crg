import { useSyncExternalStore } from 'react';
import { useMedia } from '@/shared/hooks/useMedia';

/**
 * Открыт ли предпросмотр документа (issue #193; Р9, sharedui-md3-17). Переключатель — в шапке документа, лист —
 * во вкладке реквизитов: это разные компоненты, и состояние у них общее, а выбор запоминается между
 * документами (localStorage). По умолчанию свёрнут: открыт, только если человек его открывал.
 *
 * <p>Шлюз — `xl` (1280): уже 400-пиксельный лист рядом оставлял бы форме меньше, чем ей нужно, и
 * предпросмотра там нет вовсе — ни листа, ни переключателя.</p>
 */
const KEY = 'doc-preview-open';
const listeners = new Set<() => void>();

function read(): boolean {
  try { return localStorage.getItem(KEY) === '1'; } catch { return false; }
}
let current = read();

function subscribe(fn: () => void) {
  listeners.add(fn);
  return () => { listeners.delete(fn); };
}

export function setDocumentPreviewOpen(open: boolean) {
  current = open;
  try { localStorage.setItem(KEY, open ? '1' : '0'); } catch { /* выбор живёт до перезагрузки */ }
  listeners.forEach(fn => fn());
}

export const PREVIEW_WIDE = '(min-width: 1280px)';

/** Открыт ли предпросмотр — и есть ли ему место (`xl`). */
export function useDocumentPreview(): { open: boolean; available: boolean } {
  const open = useSyncExternalStore(subscribe, () => current);
  const available = useMedia(PREVIEW_WIDE);
  return { open: open && available, available };
}
