import { useId, useRef, useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Add, Search } from '@/shared/ui/icons';
import { useCan } from '@/shared/api/access';
import { planRefusal, type EstimateRevision, type EstimateRow, type PlanRefusal } from '@/shared/api/planningEstimates';
import {
  useMatchMaterialGroup, usePlanNomenclature, useResourceCandidates, useRevisionMaterials, type MaterialGroup,
} from '@/shared/api/planningMatches';
import { Button } from '@/shared/ui/Button';
import { Checkbox } from '@/shared/ui/Checkbox';
import { Modal, ModalExit } from '@/shared/ui/Modal';
import { useToast } from '@/shared/ui/Toast';
import { apiError } from '@/shared/utils/apiError';
import { formatCount, formatQuantity } from '@/shared/format/format';
import { ruPlural } from '@/shared/utils/pluralize';
import { NewNomenclatureStep } from '@/features/costs/NewNomenclatureStep';
import { CandidatesBlock, PickerOption, type CandidatesAnswer } from '@/features/costs/PickerRows';
import { candidateRows, foundRows, type PickerRow } from '@/features/costs/pickerCandidates';
import { usePickerSelection } from '@/features/costs/pickerSelection';
import { memoryText } from './matchRows';

/**
 * Окно материала (ТЗ PLAN-9.1, макет П1 1д, «по материалу» — из варианта 3): материалы без номенклатуры по очереди,
 * одно решение — на все строки, где материал стоит (D10 `plan-31`).
 *
 * Части — окна выбора позиции счетов: кандидаты с причиной, поиск по справочнику (свой адрес модуля смет — тот же
 * поиск ядра), «Новая позиция…» шагом окна с показом похожих и только при `core.nomenclature.edit`. Карточки позиции
 * справа нет: она читается адресом счетов.
 *
 * «Выбрать и к следующему» ведёт по материалам, не закрывая окна; «Пропустить» оставляет материал ждать.
 */
export function ResourceMatchDialog({ revision, rows, onClose }: {
  revision: EstimateRevision;
  /** Строки редакции — в них ресурс материала, по которому сервер подбирает похожие. */
  rows: readonly EstimateRow[];
  onClose: () => void;
}) {
  const materials = useRevisionMaterials(revision.id);
  const match = useMatchMaterialGroup(revision.id);
  // Пройденные: сопоставленные (до перечитывания они ещё «без позиции») и пропущенные.
  const [passed, setPassed] = useState<ReadonlySet<string>>(new Set());
  const [version, setVersion] = useState<string | null>(null);

  const queue = (materials.data ?? []).filter(g => g.marks.some(m => m.code === 'unmatched'));
  const left = queue.filter(g => !passed.has(g.key));
  const current = left[0] ?? null;
  const pass = (key: string) => setPassed(prev => new Set(prev).add(key));

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title="Позиция номенклатуры" wide busy={match.isPending}>
      {materials.isError ? (
        <p role="alert" className="text-sm text-error">{apiError(materials.error, 'Материалы редакции не прочитались')}</p>
      ) : !materials.data ? (
        <p className="text-sm text-fg4">Читаем материалы редакции…</p>
      ) : current ? (
        <MaterialPick key={current.key} group={current} rows={rows} revision={revision} match={match}
          seen={version ?? revision.version} setVersion={setVersion}
          // Номер — от пройденных: перечитанная очередь сопоставленного уже не содержит, и счёт от неё
          // начинался бы заново («1 из 1» после первого же выбора).
          place={`Материал ${formatCount(passed.size + 1)} из ${formatCount(passed.size + left.length)}`}
          onDone={() => pass(current.key)} />
      ) : (
        <div className="space-y-3">
          <p className="text-sm text-on-surface-variant">
            {queue.length === 0 ? 'Материалов без номенклатуры в редакции нет.' : 'Пройдены все материалы без номенклатуры.'}
          </p>
          <div className="flex justify-end"><ModalExit variant="filled">Готово</ModalExit></div>
        </div>
      )}
    </Modal>
  );
}

/** Ресурс материала в строках редакции — по словам файла: шифр, наименование, единица. */
function resourceOf(group: MaterialGroup, rows: readonly EstimateRow[]): string | null {
  for (const row of rows)
    for (const m of row.materials)
      if (m.sourceName === group.sourceName && m.sourceCode === group.sourceCode && m.sourceUnit === group.sourceUnit) return m.id;
  return null;
}

function MaterialPick({ group, rows, revision, match, seen, setVersion, place, onDone }: {
  group: MaterialGroup;
  rows: readonly EstimateRow[];
  revision: EstimateRevision;
  match: ReturnType<typeof useMatchMaterialGroup>;
  seen: string;
  setVersion: (v: string | null) => void;
  place: string;
  onDone: () => void;
}) {
  const toast = useToast();
  const [typed, setTyped] = useState('');
  const [query, setQuery] = useState('');
  const timer = useRef<number | null>(null);
  const [remember, setRemember] = useState(true);
  const [refusal, setRefusal] = useState<PlanRefusal | null>(null);
  const [creating, setCreating] = useState(false);
  const canCreate = useCan().permission('core.nomenclature.edit');
  const listId = useId();

  const candidates = useResourceCandidates(revision.id, resourceOf(group, rows));
  const found = usePlanNomenclature(query);
  const picked = candidateRows(candidates.isPending ? undefined : candidates.data, [], null);
  const searching = typed.trim() !== '';
  const answered = searching && query !== '' ? found.data : undefined;
  const list: PickerRow[] = searching ? (answered ? foundRows(answered.items, picked.rows) : []) : picked.rows;
  const best = searching ? null : picked.rows.find(r => r.best)?.id ?? null;
  const selection = usePickerSelection(list.map(r => r.id), best);
  const unusable = candidates.data?.recalledUnusable ?? null;
  // Запомненное, которое подставить нельзя, названо здесь своими словами: блок кандидатов говорит о строке поставщика.
  // Поля — поимённо, а не копией результата: копия прочла бы все и перерисовывала бы окно на каждый перезапрос.
  const answer: CandidatesAnswer = {
    data: candidates.data && { ...candidates.data, recalledUnusable: null },
    isPending: candidates.isPending, isError: candidates.isError, isSuccess: candidates.isSuccess,
    error: candidates.error, refetch: candidates.refetch,
  };

  function type(next: string) {
    setTyped(next);
    if (timer.current !== null) window.clearTimeout(timer.current);
    timer.current = window.setTimeout(() => setQuery(next.trim()), 250);
  }

  const choose = (id: string, name: string | null) => {
    setRefusal(null);
    match.mutate({ key: group.key, nomenclatureId: id, version: seen, remember }, {
      onSuccess: done => {
        setVersion(done.version);
        toast.success(`«${group.sourceName}» → «${name ?? 'позиция без названия'}» в ${formatCount(group.rows)} `
          + `${ruPlural(group.rows, 'строке', 'строках', 'строках')}${memoryText(done.memory)}`);
        onDone();
      },
      onError: e => { setRefusal(planRefusal(e)); setVersion(null); },
    });
  };
  const active = list.find(r => r.id === selection.active) ?? null;
  const option = (row: PickerRow) => (
    <PickerOption key={row.id} row={row} id={`${listId}-${row.id}`} active={row.id === selection.active}
      enter={false} narrow={false} kinds={false} onChoose={r => choose(r.id, r.name)} onHover={selection.hover} />
  );

  return (
    <div className="space-y-3">
      <div className="rounded-lg bg-surface-container px-3 py-2 space-y-0.5">
        <p className="text-caption text-fg3">{place} · ред. {revision.number}</p>
        <p className="text-sm text-on-surface">{group.sourceName}</p>
        <p className="text-xs text-fg3 tabular-nums">
          {[group.sourceCode, `${formatQuantity(group.quantity)} ${group.sourceUnit ?? ''}`.trim(),
            `в ${formatCount(group.rows)} ${ruPlural(group.rows, 'строке', 'строках', 'строках')}`].filter(Boolean).join(' · ')}
        </p>
      </div>

      <div className="flex items-center gap-2 rounded-md border border-outline px-2.5 h-10">
        <Icon icon={Search} size={18} className="text-fg4 shrink-0" />
        <input value={typed} onChange={e => type(e.target.value)} placeholder="наименование или часть наименования"
          aria-label="Поиск позиции" className="flex-1 bg-transparent text-sm outline-none text-on-surface placeholder:text-fg4" />
      </div>

      {unusable && (
        <p className="text-xs text-fg3">
          {unusable.issue === 'archived'
            ? `Для этого материала запомнена позиция «${unusable.name ?? 'без названия'}» — она в архиве. Выберите действующую.`
            : 'Для этого материала запомнена позиция, которой в справочнике больше нет. Выберите другую.'}
        </p>
      )}

      <div onMouseLeave={selection.leave}>
        {searching ? (
          <div className="space-y-2">
            {found.isError && <p role="alert" className="text-xs text-error">Справочник не прочитан: {apiError(found.error, 'сервер отказал')}. Это не «позиций нет».</p>}
            <div id={listId} role="listbox" aria-label="Найденные позиции" className="-mx-2 space-y-0.5">{list.map(option)}</div>
            {answered && list.length === 0 && <p className="text-xs text-fg4">По этому запросу позиций нет. Проверьте другое написание.</p>}
            {!!answered?.inArchive && <p className="text-xs text-fg3">В архиве под этот запрос: {answered.inArchive} — заводить заново не нужно.</p>}
            {answered?.more && (
              <p className="text-xs text-warning">Показаны первые {list.length} — подходящих больше. Уточните запрос.</p>
            )}
          </div>
        ) : (
          <CandidatesBlock asked answer={answer} rows={picked.rows.length} more={picked.more} current={null} headingId={`${listId}-h`}>
            {picked.rows.length > 0 && (
              <div id={listId} role="listbox" aria-labelledby={`${listId}-h`} className="-mx-2 space-y-0.5">{picked.rows.map(option)}</div>
            )}
          </CandidatesBlock>
        )}
      </div>

      <Checkbox checked={remember} onChange={setRemember}
        label={group.sourceCode ? 'Запомнить по коду ресурса — следующий импорт подставит сам' : 'Запомнить по наименованию — следующий импорт подставит сам'} />
      {refusal && <p role="alert" className="text-sm text-error">{refusal.text}</p>}

      {creating && (
        <NewNomenclatureStep from={{ name: group.sourceName, code: group.sourceCode, unit: group.sourceUnit }}
          plate={<p className="text-sm text-on-surface">{group.sourceName}</p>} narrow
          onBack={() => setCreating(false)} onPick={(id, name) => { setCreating(false); choose(id, name); }} />
      )}

      <div className="flex flex-wrap items-center justify-end gap-2 border-t border-outline-variant pt-3">
        {canCreate && (
          <Button size="sm" variant="text" icon={<Icon icon={Add} size={18} />} className="mr-auto" onClick={() => setCreating(true)}>Новая позиция…</Button>
        )}
        <ModalExit>Закрыть</ModalExit>
        <Button variant="text" disabled={match.isPending} onClick={onDone}>Пропустить</Button>
        <Button variant="filled" disabled={!active} loading={match.isPending} onClick={() => active && choose(active.id, active.name)}>
          Выбрать и к следующему
        </Button>
      </div>
    </div>
  );
}
