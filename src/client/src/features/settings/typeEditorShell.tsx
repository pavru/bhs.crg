import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { ChevronDown, ChevronUp } from 'lucide-react';

// Оформительские куски страниц-редакторов типа list-detail (issue #197 / #210): диалог-гард
// при уходе с несохранёнными правками и свёрнутая карточка-секция. Сам реестр редакторов —
// в `typeEditorRegistry.ts` (issue #858: компонент и хуки в одном модуле лишают файл горячей
// подмены).

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
