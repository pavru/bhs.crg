import { createContext, useContext, useRef, useEffect, useState, useMemo } from 'react';
import { ChevronDown, ChevronUp } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';

// ─── Реестр редакторов (явное сохранение, issue #197 / #210) ─────────────────────
// Общий для страниц-редакторов типа list-detail (Типы документов, Типы полей). Дочерние формы
// публикуют своё состояние dirty и функцию сохранения; страница агрегирует их в одну кнопку
// «Сохранить» в шапке, бейдж «есть изменения» и диалог-гард при уходе. Сохранение может бросить —
// тогда переход не выполняется. Полноценный ListDetailShell извлечём позже (после 3-й страницы).
export interface TypeEditorRegistry {
  publish: (key: string, dirty: boolean, save: () => Promise<void>) => void;
  unpublish: (key: string) => void;
}
const TypeEditorContext = createContext<TypeEditorRegistry | null>(null);
export const TypeEditorProvider = TypeEditorContext.Provider;

/** Публикует dirty/save текущей формы в реестр страницы (save всегда берётся свежий через ref). */
export function useRegisterEditor(key: string, dirty: boolean, save: () => Promise<void>) {
  const ctx = useContext(TypeEditorContext);
  const saveRef = useRef(save);
  saveRef.current = save;
  useEffect(() => {
    ctx?.publish(key, dirty, () => saveRef.current());
  }, [ctx, key, dirty]);
  useEffect(() => () => ctx?.unpublish(key), [ctx, key]);
}

/** Агрегатор реестра для корня list-detail страницы: dirty-состояние + saveAll + provider value. */
export function useTypeEditorRegistry() {
  const [dirtyMap, setDirtyMap] = useState<Record<string, boolean>>({});
  const saversRef = useRef<Record<string, () => Promise<void>>>({});
  const registry = useMemo<TypeEditorRegistry>(() => ({
    publish: (key, dirty, save) => {
      saversRef.current[key] = save;
      setDirtyMap(m => m[key] === dirty ? m : { ...m, [key]: dirty });
    },
    unpublish: (key) => {
      delete saversRef.current[key];
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
  return { registry, anyDirty, saving, saveAll };
}

/** MD3-диалог-гард при уходе с выбранного элемента с несохранёнными правками. */
export function LeaveGuardDialog({ open, saving, onSave, onDiscard, onCancel }: {
  open: boolean; saving: boolean;
  onSave: () => void; onDiscard: () => void; onCancel: () => void;
}) {
  return (
    <Modal open={open} onOpenChange={o => { if (!o && !saving) onCancel(); }} title="Несохранённые изменения"
      footer={
        <div className="flex items-center justify-end gap-2">
          <Button variant="text" onClick={onCancel} disabled={saving}>Отмена</Button>
          <Button variant="tonal" onClick={onDiscard} disabled={saving}>Не сохранять</Button>
          <Button variant="filled" onClick={onSave} loading={saving}>Сохранить и перейти</Button>
        </div>
      }>
      <p className="text-sm text-fg2">
        Есть несохранённые изменения. Сохранить их перед переходом к другому элементу?
      </p>
    </Modal>
  );
}

/** Свёрнутая MD3-карточка-секция: заголовок с иконкой/счётчиком/chevron + раскрывающееся тело. */
export function SectionCard({ icon, title, count, countClass, open, onToggle, children }: {
  icon: React.ReactNode;
  title: string;
  count?: number;
  countClass?: string;
  open: boolean;
  onToggle: () => void;
  children: React.ReactNode;
}) {
  return (
    <div className="border border-stroke rounded-lg bg-surface overflow-hidden">
      <button type="button" onClick={onToggle}
        className="w-full flex items-center gap-2 px-3 py-2.5 hover:bg-muted/40 transition-colors">
        <span className="text-fg4 shrink-0">{icon}</span>
        <span className="text-sm font-medium text-fg2">{title}</span>
        {count != null && count > 0 && <span className={`text-xs ${countClass ?? 'text-brand'}`}>({count})</span>}
        <span className="flex-1" />
        {open ? <ChevronUp size={16} className="text-fg4 shrink-0" /> : <ChevronDown size={16} className="text-fg4 shrink-0" />}
      </button>
      {open && <div className="px-3 pb-3 pt-1 border-t border-stroke">{children}</div>}
    </div>
  );
}
