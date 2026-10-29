import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Database, Description, LinkIcon, LinkOff, Warning } from '@/shared/ui/icons';
import { Modal } from '@/shared/ui/Modal';
import { type BaseCandidate } from './baseCandidates';
import { Tooltip } from '@/shared/ui/Tooltip';

// Базовый экземпляр (issue #71/#73): объект дочернего типа наследуется от базы — документа комплекта
// ЛИБО записи общих данных (по цепочке типов-предков и скоп-близости). Единый пикер + общие хелперы
// кандидатов — переиспользуются редактором документов, каталогом и системным каталогом (шаг 2 #73).

interface BaseCandidatePickerProps {
  open: boolean; onOpenChange: (o: boolean) => void;
  candidates: BaseCandidate[]; onSelect: (c: BaseCandidate) => void;
}

/**
 * Единый пикер базового экземпляра — презентационный: получает уже готовый (обычно отсортированный
 * по близости) список кандидатов и вызывает onSelect. Вычисление и формат хранения `_baseRef` —
 * ответственность вызывающего (документ пишет {kind,id}; каталог/система — голый id).
 *
 * <p>Тело монтируется по открытию (issue #858). Пикер держат формы (<code>RequisitesTab</code>,
 * <code>BaseInstanceChip</code>), то есть он смонтирован постоянно, и набранный поиск переживал
 * закрытие: следующее открытие молча показывало отфильтрованный список без единого намёка на
 * причину. Ровно этот дефект чинился в <code>RefPickerModal</code>; здесь он оставался (поймано
 * ревью PR #861).</p>
 */
export function BaseCandidatePicker(props: BaseCandidatePickerProps) {
  return props.open ? <BaseCandidatePickerBody {...props} /> : null;
}

function BaseCandidatePickerBody({ onOpenChange, candidates, onSelect }: BaseCandidatePickerProps) {
  const [search, setSearch] = useState('');
  const [active, setActive] = useState(0);
  const filtered = candidates.filter(c => c.name.toLowerCase().includes(search.toLowerCase()));
  // issue #89: две именованные группы — «роль на реальный объект того же типа» и наследование от базового типа.
  const proxyCands = filtered.filter(c => c.proxy);
  const baseCands = filtered.filter(c => !c.proxy);
  // Единый порядок для клавиатурной навигации (issue #107 F5): proxy-группа, затем базовые.
  const ordered = [...proxyCands, ...baseCands];

  function pick(c: BaseCandidate) { onSelect(c); onOpenChange(false); }
  function onKey(e: React.KeyboardEvent) {
    if (e.key === 'ArrowDown') { e.preventDefault(); setActive(a => Math.min(a + 1, ordered.length - 1)); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setActive(a => Math.max(a - 1, 0)); }
    else if (e.key === 'Enter') { e.preventDefault(); const c = ordered[active]; if (c) pick(c); }
  }

  const row = (c: BaseCandidate, gi: number) => {
    const on = gi === active;
    return (
      <button key={`${c.kind}:${c.id}`} type="button" role="option" aria-selected={on} id={`bcp-opt-${gi}`}
        onMouseEnter={() => setActive(gi)} onClick={() => pick(c)}
        className={`w-full flex items-center gap-3 px-3 py-2 text-body-medium text-left rounded-sm transition-colors ${
          on ? 'bg-secondary-container text-on-secondary-container' : 'hover:bg-primary-container'}`}>
        {c.proxy
          ? <Icon icon={LinkIcon} size={18} className={`shrink-0 ${on ? 'text-on-secondary-container' : 'text-primary'}`} />
          : c.kind === 'instance'
            ? <Icon icon={Description} size={18} className={`shrink-0 ${on ? 'text-on-secondary-container' : 'text-primary'}`} />
            : <Icon icon={Database} size={18} className={`shrink-0 ${on ? 'text-on-secondary-container' : 'text-primary'}`} />}
        <span className={`flex-1 font-medium truncate ${on ? 'text-on-secondary-container' : 'text-on-surface'}`}>{c.name}</span>
        {c.targetIsProxy && <span className="text-label-small text-warning shrink-0">роль</span>}
        <span className={`text-label-small shrink-0 ${on ? 'text-on-secondary-container/80' : 'text-on-surface-variant'}`}>{c.scopeLabel}</span>
      </button>
    );
  };

  return (
    <Modal open onOpenChange={onOpenChange} title="Базовый экземпляр / роль" nothingToLose>
      <div className="space-y-4">
        {/* Подсветка возвращается на первую строку в ОБРАБОТЧИКЕ ввода, а не эффектом (issue #858):
            сброс — следствие набора текста, а не состояния, которое надо с чем-то синхронизировать.
            Эффектом это давало лишний коммит, в котором active ещё указывал в старый список. */}
        <input value={search} onChange={e => { setSearch(e.target.value); setActive(0); }} onKeyDown={onKey}
          placeholder="Поиск…" autoFocus role="combobox" aria-expanded aria-controls="bcp-listbox"
          aria-activedescendant={ordered.length ? `bcp-opt-${active}` : undefined}
          className="w-full border border-outline rounded-sm px-3 py-2 text-body-medium focus:outline-none focus-visible:ring-2 focus-visible:ring-primary bg-surface" />
        {filtered.length === 0 ? (
          <p className="text-body-medium text-on-surface-variant text-center py-4">Нет подходящих кандидатов.</p>
        ) : (
          <div id="bcp-listbox" role="listbox" aria-label="Кандидаты" className="space-y-3 max-h-72 overflow-y-auto">
            {proxyCands.length > 0 && (
              <div className="space-y-1">
                <p className="text-title-small text-on-surface-variant px-1">Роль на реальный объект (тот же тип)</p>
                {proxyCands.map((c, i) => row(c, i))}
              </div>
            )}
            {baseCands.length > 0 && (
              <div className="space-y-1">
                {proxyCands.length > 0 && <p className="text-title-small text-on-surface-variant px-1">Базовый тип</p>}
                {baseCands.map((c, j) => row(c, proxyCands.length + j))}
              </div>
            )}
          </div>
        )}
      </div>
    </Modal>
  );
}

/**
 * Панель «Базовый экземпляр» (issue #73, шаг 3) — единый блок для всех форм объекта составного типа
 * (редактор документов, каталог, системный каталог). Показывает выбранную базу / кнопку выбора /
 * состояние «недоступна», содержит пикер. Вычисление кандидатов и формат хранения `_baseRef` —
 * ответственность вызывающего (см. BaseCandidatePicker).
 */
export function BaseInstancePanel({ title, candidates, selected, missing = false, manualHint, onSelect, onClear }: {
  title?: string;                       // напр. имя родительского типа — в подзаголовок
  candidates: BaseCandidate[];
  selected: BaseCandidate | undefined;  // выбранная база (по _baseRef), если найдена среди кандидатов
  missing?: boolean;                    // ссылка задана, но кандидат не найден (удалён/вне видимости)
  manualHint?: string;                  // подсказка «без базы все N полей вручную»
  onSelect: (c: BaseCandidate) => void;
  onClear: () => void;
}) {
  const [pickerOpen, setPickerOpen] = useState(false);
  return (
    <div className="rounded-lg border border-outline-variant bg-surface p-4 space-y-2">
      <p className="text-body-medium font-medium text-on-surface">
        {selected?.proxy ? 'Роль — ссылка на реальный объект' : 'Базовый экземпляр'}
        {title && !selected?.proxy && <span className="font-normal ml-1 text-on-surface-variant">({title})</span>}
      </p>
      {selected ? (
        <>
        <div className="flex items-center gap-2 rounded-sm border border-primary-container bg-primary-container px-3 py-2">
          {selected.proxy
            ? <Icon icon={LinkIcon} size={18} className="text-primary shrink-0" />
            : selected.kind === 'instance'
              ? <Icon icon={Description} size={18} className="text-primary shrink-0" />
              : <Icon icon={Database} size={18} className="text-primary shrink-0" />}
          <span className="flex-1 text-body-medium font-medium text-primary truncate">
            {selected.proxy && <span className="text-on-surface-variant font-normal">→ </span>}{selected.name}
          </span>
          <span className="text-label-small text-on-surface-variant shrink-0">{selected.scopeLabel}</span>
          <Tooltip content="Снять ссылку" name>
            <button type="button" onClick={onClear} className="text-primary hover:text-error transition-colors">
              <Icon icon={LinkOff} size={18} />
            </button>
          </Tooltip>
        </div>
        {selected.targetIsProxy && (
          <p className="text-label-small text-warning flex items-center gap-1">
            <Icon icon={Warning} size={18} className="shrink-0" />
            «{selected.name}» — тоже роль. Цепочка ссылок усложняет прослеживание данных.
          </p>
        )}
        </>
      ) : missing ? (
        <div className="flex items-center gap-2 rounded-sm border border-warning/40 bg-warning/5 px-3 py-2">
          <span className="flex-1 text-body-medium text-warning truncate">Базовый экземпляр недоступен (удалён или вне области видимости)</span>
          <Tooltip content="Снять ссылку" name>
            <button type="button" onClick={onClear} className="text-primary hover:text-error transition-colors">
              <Icon icon={LinkOff} size={18} />
            </button>
          </Tooltip>
        </div>
      ) : (
        <button type="button" onClick={() => setPickerOpen(true)}
          className="flex items-center gap-2 text-body-medium text-primary border border-dashed border-primary-container rounded-sm px-3 py-2 w-full hover:bg-primary-container transition-colors">
          <Icon icon={LinkIcon} size={18} />
          Выбрать базовый экземпляр или роль...
        </button>
      )}
      {!selected && !missing && manualHint && <p className="text-body-small text-on-surface-variant">{manualHint}</p>}
      <BaseCandidatePicker open={pickerOpen} onOpenChange={setPickerOpen} candidates={candidates} onSelect={onSelect} />
    </div>
  );
}
