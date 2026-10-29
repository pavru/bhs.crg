import { Modal } from './Modal';

/** Шпаргалка по горячим клавишам (issue #107) — открывается клавишей «?». */
const SHORTCUTS: [string, string][] = [
  ['Ctrl / ⌘ + K', 'Командная палитра — переход к разделам и действия'],
  ['?', 'Эта справка по горячим клавишам'],
  ['↑ ↓ + Enter', 'Навигация по спискам и пикерам (палитра, выбор объекта)'],
  ['← →', 'Переключение вкладок в редакторе документа'],
  ['Esc', 'Закрыть палитру, просмотр или пустой диалог; в форме с набранным — перейти к кнопке выхода'],
  ['Tab', 'Переход между полями формы'],
  ['Ctrl / ⌘ + − / + / 0', 'Масштаб листа в просмотре PDF (и Ctrl + колесо) — когда фокус или указатель над листом'],
  ['Ctrl / ⌘ + P', 'В просмотре PDF — печать самого файла, а не страницы'],
  ['Home End', 'В просмотре PDF — первая и последняя страница'],
];

export function ShortcutsHelp({ open, onOpenChange }: { open: boolean; onOpenChange: (o: boolean) => void }) {
  return (
    <Modal open={open} onOpenChange={onOpenChange} title="Горячие клавиши" nothingToLose>
      <div className="divide-y divide-surface-container">
        {SHORTCUTS.map(([keys, desc]) => (
          <div key={keys} className="flex items-center gap-4 py-2">
            <kbd className="min-w-[120px] shrink-0 rounded-xs bg-surface-container px-2 py-1 text-center text-xs font-medium text-on-surface-variant">
              {keys}
            </kbd>
            <span className="text-sm text-on-surface-variant">{desc}</span>
          </div>
        ))}
      </div>
    </Modal>
  );
}
