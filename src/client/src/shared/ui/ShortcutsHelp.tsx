import { Modal } from './Modal';

/** Шпаргалка по горячим клавишам (issue #107) — открывается клавишей «?». */
const SHORTCUTS: [string, string][] = [
  ['Ctrl / ⌘ + K', 'Командная палитра — переход к разделам и действия'],
  ['?', 'Эта справка по горячим клавишам'],
  ['↑ ↓ + Enter', 'Навигация по спискам и пикерам (палитра, выбор объекта)'],
  ['← →', 'Переключение вкладок в редакторе документа'],
  ['Esc', 'Закрыть диалог или палитру'],
  ['Tab', 'Переход между полями формы'],
];

export function ShortcutsHelp({ open, onOpenChange }: { open: boolean; onOpenChange: (o: boolean) => void }) {
  return (
    <Modal open={open} onOpenChange={onOpenChange} title="Горячие клавиши">
      <div className="divide-y divide-muted">
        {SHORTCUTS.map(([keys, desc]) => (
          <div key={keys} className="flex items-center gap-4 py-2">
            <kbd className="min-w-[120px] shrink-0 rounded bg-muted px-2 py-1 text-center text-xs font-medium text-fg2">
              {keys}
            </kbd>
            <span className="text-sm text-fg2">{desc}</span>
          </div>
        ))}
      </div>
    </Modal>
  );
}
