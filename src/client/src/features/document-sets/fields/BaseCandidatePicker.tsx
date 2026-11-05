import { useEffect, useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Database, Description, LinkIcon, Warning } from '@/shared/ui/icons';
import { Modal } from '@/shared/ui/Modal';
import { type BaseCandidate } from './baseCandidates';
import { Card } from '@/shared/ui/Card';
import { Chip } from '@/shared/ui/Chip';
import { StatusText } from '@/shared/ui/StatusText';
import { TextField } from '@/shared/ui/TextField';
import { ListOption } from '@/shared/ui/ListOption';

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

  const row = (c: BaseCandidate, gi: number) => (
    <ListOption key={`${c.kind}:${c.id}`} id={`bcp-opt-${gi}`} active={gi === active}
      icon={c.proxy ? LinkIcon : c.kind === 'instance' ? Description : Database} iconTone="text-primary" headline={c.name}
      trailing={<>{c.targetIsProxy && <span className="text-warning">роль</span>}{c.scopeLabel}</>}
      onHover={() => setActive(gi)} onChoose={() => pick(c)} />
  );
  // Две группы — два listbox, каждый подписан своим заголовком (Р7): заголовок внутри listbox был бы его
  // запрещённым ребёнком. Поле поиска показывает на тот, где стоит активный.
  const activeList = active < proxyCands.length ? 'bcp-proxy' : 'bcp-base';
  // Активный — на виду: список прокручивается, и подсветка ушла бы за край.
  useEffect(() => { document.getElementById(`bcp-opt-${active}`)?.scrollIntoView?.({ block: 'nearest' }); }, [active]);

  return (
    <Modal open onOpenChange={onOpenChange} title="Базовый экземпляр / роль" nothingToLose>
      <div className="space-y-4">
        {/* Подсветка возвращается на первую строку в ОБРАБОТЧИКЕ ввода, а не эффектом (issue #858):
            сброс — следствие набора текста, а не состояния, которое надо с чем-то синхронизировать.
            Эффектом это давало лишний коммит, в котором active ещё указывал в старый список. */}
        <TextField label="Поиск кандидата" value={search} onChange={e => { setSearch(e.target.value); setActive(0); }} onKeyDown={onKey}
          autoFocus role="combobox" aria-expanded aria-controls={ordered.length ? activeList : undefined}
          aria-activedescendant={ordered.length ? `bcp-opt-${active}` : undefined} />
        {filtered.length === 0 ? (
          <p className="text-body-medium text-on-surface-variant text-center py-4">Нет подходящих кандидатов.</p>
        ) : (
          <div className="-mx-4 space-y-3 max-h-72 overflow-y-auto">
            {proxyCands.length > 0 && (
              <div>
                <p id="bcp-proxy-h" className="text-title-small text-on-surface-variant px-4 mb-1">Роль на реальный объект (тот же тип)</p>
                <div id="bcp-proxy" role="listbox" aria-labelledby="bcp-proxy-h">{proxyCands.map((c, i) => row(c, i))}</div>
              </div>
            )}
            {baseCands.length > 0 && (
              <div>
                {proxyCands.length > 0 && <p id="bcp-base-h" className="text-title-small text-on-surface-variant px-4 mb-1">Базовый тип</p>}
                <div id="bcp-base" role="listbox" {...(proxyCands.length > 0 ? { 'aria-labelledby': 'bcp-base-h' } : { 'aria-label': 'Кандидаты' })}>
                  {baseCands.map((c, j) => row(c, proxyCands.length + j))}
                </div>
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
    <Card title={<>
      {selected?.proxy ? 'Роль — ссылка на реальный объект' : 'Базовый экземпляр'}
      {title && !selected?.proxy && <span className="ml-1 text-on-surface-variant">({title})</span>}
    </>}>
      <div className="space-y-2">
      {selected ? (
        <>
        {/* Выбранная база — input chip со знаком вида (Р4): щелчок — выбрать другую, крестик — снять. */}
        <div className="flex flex-wrap items-center gap-2">
          <Chip icon={selected.proxy ? LinkIcon : selected.kind === 'instance' ? Description : Database}
            title="Выбрать другой" onClick={() => setPickerOpen(true)} onRemove={onClear} removeLabel="Снять ссылку"
            className="max-w-full">
            {selected.proxy && '→ '}{selected.name}
          </Chip>
          <StatusText>{selected.scopeLabel}</StatusText>
        </div>
        {selected.targetIsProxy && (
          <p className="text-label-small text-warning flex items-center gap-1">
            <Icon icon={Warning} size={18} className="shrink-0" />
            «{selected.name}» — тоже роль. Цепочка ссылок усложняет прослеживание данных.
          </p>
        )}
        </>
      ) : missing ? (
        <Chip icon={Warning} iconTone="warning" iconLabel="Недоступен" title="Выбрать другой" onClick={() => setPickerOpen(true)}
          onRemove={onClear} removeLabel="Снять ссылку" className="max-w-full">
          Базовый экземпляр недоступен (удалён или вне области видимости)
        </Chip>
      ) : (
        <button type="button" onClick={() => setPickerOpen(true)}
          className="flex items-center gap-2 text-body-medium text-primary border border-dashed border-primary-container rounded-sm px-3 py-2 w-full hover:bg-primary-container transition-colors">
          <Icon icon={LinkIcon} size={18} />
          Выбрать базовый экземпляр или роль...
        </button>
      )}
      {!selected && !missing && manualHint && <p className="text-body-small text-on-surface-variant">{manualHint}</p>}
      </div>
      <BaseCandidatePicker open={pickerOpen} onOpenChange={setPickerOpen} candidates={candidates} onSelect={onSelect} />
    </Card>
  );
}
