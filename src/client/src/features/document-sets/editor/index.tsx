import { useState, useEffect, useRef, useMemo } from 'react';
import { Pencil, Database, X } from 'lucide-react';
import { DocumentObservationsChip } from './DocumentObservations';
import { useRenameDocumentInstance, useResolutionDiagnostics, brokenRefPaths } from '@/shared/api/documentSets';
import { useListDataSetBindings } from '@/shared/api/datasets';
import { FUNCTIONAL_TAG } from '@/shared/api/tags';
import type { DocumentInstance, DocumentType } from '@/shared/api/types';
import { resolveEffectiveFields, compositeFieldHasTag } from '@/shared/api/schema';
import { STATUS_LABELS, STATUS_COLORS, BaseInstanceChip, type BaseCandidate } from '../fields';
import { ruCount } from '@/shared/utils/pluralize';
import { DataSetsTab } from './DataSetsTab';
import { QualityLinksTab } from './QualityLinksTab';
import { Modal } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { containsRef } from './brokenRefs';
import { BrokenCountBadge } from './BrokenCountBadge';
import { RequisitesTab } from './RequisitesTab';
import { GenerationTab } from './GenerationTab';
import { useUploadsInFlight } from '@/shared/ui/uploadsInFlight';

// ── Редактор экземпляра документа: оболочка вкладок ──────────────────────────
// Сами вкладки вынесены по файлам (#490).

function InstanceNameEditor({ instance, setId, docType }: {
  instance: DocumentInstance; setId: string; docType: DocumentType | undefined;
}) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState('');
  const rename = useRenameDocumentInstance();

  function start() { setDraft(instance.name ?? ''); setEditing(true); }
  function save() {
    const trimmed = draft.trim();
    if (trimmed !== (instance.name ?? '')) {
      rename.mutate({ setId, instanceId: instance.id, name: trimmed });
    }
    setEditing(false);
  }

  if (editing) {
    return (
      <input
        value={draft}
        onChange={e => setDraft(e.target.value)}
        onBlur={save}
        onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); save(); } if (e.key === 'Escape') setEditing(false); }}
        autoFocus
        placeholder={docType?.name ?? 'Название документа'}
        className="text-sm font-medium text-fg1 bg-transparent border-b border-brand outline-none w-full min-w-0"
      />
    );
  }

  return (
    <button type="button" onClick={start} className="flex items-center gap-1.5 group/name text-left min-w-0 max-w-full">
      <span className={`text-sm font-medium truncate ${instance.name ? 'text-fg1' : 'text-fg3'}`}>
        {instance.name || docType?.name || 'Документ'}
      </span>
      <Pencil size={12} className="text-fg4 shrink-0 opacity-0 group-hover/name:opacity-100 transition-opacity" />
    </button>
  );
}

// ─── Instance editor modal ────────────────────────────────────────────────────

type InstanceTab = 'requisites' | 'quality' | 'generation';

export function InstanceEditor({ instance, setId, docType, allDocTypes, otherInstances, onClose, onDirtyChange, requestClose }: {
  instance: DocumentInstance; setId: string; docType: DocumentType | undefined;
  allDocTypes: DocumentType[]; otherInstances: DocumentInstance[]; onClose: () => void;
  onDirtyChange?: (dirty: boolean) => void;
  /** Закрытие с guard несохранённых изменений (крестик top app bar). */
  requestClose?: () => void;
}) {
  const schemaFields = docType ? resolveEffectiveFields(docType, allDocTypes) : [];
  const [tab, setTab] = useState<InstanceTab>('requisites');
  // Свод битых ссылок для бейджа на вкладке «Реквизиты» (issue #334). Общий кэш с индикаторами полей
  // (тот же queryKey) — без второго запроса; гейт по наличию ссылок в сохранённых реквизитах.
  const { data: editorDiagnostics } = useResolutionDiagnostics(instance.id, containsRef(instance.requisites));
  const brokenTabCount = useMemo(() => brokenRefPaths(editorDiagnostics).size, [editorDiagnostics]);
  // Устаревшие источники документа (issue #815) — счёт по ИСТОЧНИКАМ, а не по полям: действие
  // («Перераспознать») применяется к источнику, и «устарели источники: 2» отвечает на вопрос
  // «сколько раз мне это делать». Признак висит здесь потому, что шапка видна всегда: секция
  // связанных полей свёрнута по умолчанию, а из разделов формы виден только открытый.
  const { data: docBindings = [] } = useListDataSetBindings({ ownerId: instance.id });
  const staleSourceCount = useMemo(
    () => new Set(docBindings.filter(b => b.source?.recognitionStale).map(b => b.sourceId)).size,
    [docBindings]);
  const [dataSourcesOpen, setDataSourcesOpen] = useState(false);
  const [dirty, setDirty] = useState(false);
  const [pendingTab, setPendingTab] = useState<InstanceTab | null>(null);
  const [switching, setSwitching] = useState(false);
  const [saving, setSaving] = useState(false);
  const [savedFlash, setSavedFlash] = useState(false);
  // Картинка ещё едет в хранилище — сохранять рано: значение поля появится только после (issue #522).
  const uploading = useUploadsInFlight();
  // Актуальная функция сохранения активной редактируемой вкладки.
  const saveRef = useRef<(() => Promise<boolean>) | null>(null);
  // «Основа» (issue #223): состояние-зеркало базы для chip шапки — источник правды `_baseRef` живёт в
  // `values` внутри RequisitesTab, сюда синкается для отрисовки. Управление — через baseControlRef
  // (доступно только пока вкладка реквизитов смонтирована).
  const [baseState, setBaseState] = useState<{
    hasBase: boolean; selected: BaseCandidate | undefined; missing: boolean;
    candidates: BaseCandidate[]; coveredCount: number;
  } | null>(null);
  const baseControlRef = useRef<{ select: (c: BaseCandidate) => void; clear: () => void } | null>(null);

  // Редактируемые вкладки (есть что сохранять на уровне документа).
  const editable = tab === 'requisites';
  async function doSave(): Promise<boolean> {
    if (!saveRef.current) return true; // на этой вкладке нечего сохранять
    setSaving(true);
    try {
      const ok = await saveRef.current();
      if (ok) { setSavedFlash(true); setTimeout(() => setSavedFlash(false), 2000); }
      return ok;
    } finally { setSaving(false); }
  }
  async function doSaveAndClose() {
    if (editable) { if (await doSave()) onClose(); }
    else onClose();
  }

  // Прокидываем «грязное» состояние наверх — для защиты от закрытия модалки (X/Esc/клик вне).
  useEffect(() => { onDirtyChange?.(dirty); }, [dirty, onDirtyChange]);

  // Вкладка «Документы качества» — только для типов, требующих их (есть материал-массив
  // с полем-ссылкой на документ качества, тэг material.qualityDocLink).
  const requiresQuality = !!docType && compositeFieldHasTag(docType, FUNCTIONAL_TAG.materialQualityDocLink, allDocTypes);
  const tabs: [InstanceTab, string][] = [
    ['requisites', 'Реквизиты'],
    ...(requiresQuality ? [['quality', 'Документы качества'] as [InstanceTab, string]] : []),
    ['generation', 'Генерация'],
  ];

  const tabRefs = useRef<(HTMLButtonElement | null)[]>([]);
  function requestTab(next: InstanceTab) {
    if (next === tab) return;
    if (dirty) setPendingTab(next);   // есть несохранённые изменения — спрашиваем
    else setTab(next);
  }
  // APG-tablist (issue #107 F3): стрелки/Home/End двигают ФОКУС между вкладками (manual
  // activation — переключение по Enter/Space через onClick, чтобы dirty-guard не срабатывал на скролле).
  function onTabKey(e: React.KeyboardEvent, i: number) {
    let ni = -1;
    if (e.key === 'ArrowRight') ni = (i + 1) % tabs.length;
    else if (e.key === 'ArrowLeft') ni = (i - 1 + tabs.length) % tabs.length;
    else if (e.key === 'Home') ni = 0;
    else if (e.key === 'End') ni = tabs.length - 1;
    else return;
    e.preventDefault();
    tabRefs.current[ni]?.focus();
  }
  function switchTo(next: InstanceTab) {
    setDirty(false);
    setTab(next);
    setPendingTab(null);
  }
  // Тот же запрет, что у кнопок и горячей клавиши: диалог «есть несохранённое» при переключении
  // вкладки сохранял бы документ без ещё не доехавшей картинки (issue #532).
  async function saveThenSwitch() {
    if (uploading) return;
    if (!pendingTab) return;
    setSwitching(true);
    try {
      const ok = await saveRef.current?.();
      if (ok) switchTo(pendingTab);   // успех → переходим, редактор НЕ закрываем
      else setPendingTab(null);       // ошибка валидации → закрываем диалог, чтобы её было видно
    } finally { setSwitching(false); }
  }

  return (
    <div className="flex flex-col min-h-0 flex-1"
      onKeyDown={e => {
        // Ctrl/⌘+Enter — сохранить и закрыть (issue #107 F7); работает из любого поля вкладки реквизитов.
        // uploading — не косметика: горячая клавиша объявлена в подсказке самой кнопки, и без
        // проверки она сохраняла бы документ БЕЗ картинки, пока та ещё едет (issue #532).
        if ((e.ctrlKey || e.metaKey) && e.key === 'Enter' && editable && !saving && !uploading) {
          e.preventDefault();
          void doSaveAndClose();
        }
      }}>
      {/* MD3 top app bar: крестик слева, имя+подзаголовок, статус, действия справа */}
      <div className="shrink-0 bg-surface">
        <div className="flex items-center gap-3 h-16 px-3 sm:px-4">
          <button type="button" onClick={() => (requestClose ?? onClose)()} aria-label="Закрыть"
            className="flex items-center justify-center w-11 h-11 shrink-0 rounded-full text-fg3 hover:text-fg1 hover:bg-black/5 dark:hover:bg-white/10 transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand">
            <X size={20} />
          </button>
          <div className="flex-1 min-w-0">
            <InstanceNameEditor instance={instance} setId={setId} docType={docType} />
            <p className="text-xs text-fg4 mt-0.5 truncate">
              {docType?.name ? `${docType.name} · Редактирование` : 'Редактирование'}
              {baseState?.selected && baseState.coveredCount > 0 &&
                ` · ${ruCount(baseState.coveredCount, 'поле', 'поля', 'полей')} из основы`}
            </p>
          </div>
          {baseState?.hasBase && (
            <BaseInstanceChip
              selected={baseState.selected} missing={baseState.missing} candidates={baseState.candidates}
              editable={tab === 'requisites'}
              onSelect={c => baseControlRef.current?.select(c)}
              onClear={() => baseControlRef.current?.clear()} />
          )}
          <span className={`text-xs px-2 py-0.5 rounded-full font-medium shrink-0 ${STATUS_COLORS[instance.status] ?? 'bg-brand-subtle text-brand'}`}>
            {STATUS_LABELS[instance.status] ?? instance.status}
          </span>
          {/* Замечания внешнего анализа, упоминающие ЭТОТ документ (issue #456). Находок сверки
              здесь нет: их связь с документом слабее, чем выглядит. */}
          <DocumentObservationsChip setId={instance.documentSetId} documentId={instance.id} />
          {/* Источники данных (issue #296, фаза 3): пакетные операции уровня документа — обзор
              привязок, «Проверить данные», «Из шаблона». Точечная привязка — на самих полях. */}
          <Button variant="text" size="sm" icon={<Database size={15} />} onClick={() => setDataSourcesOpen(true)}
            className="shrink-0"
            title={staleSourceCount > 0
              ? `${ruCount(staleSourceCount, 'источник устарел', 'источника устарели', 'источников устарели')}: данные от прежнего файла. Откройте, чтобы перераспознать.`
              : 'Обзор привязок, проверка данных, шаблоны'}
            // На узком окне подпись скрыта и кнопка становится icon-only, а title доступен только
            // мышью — счётчик обязан попасть в доступное имя, иначе для клавиатуры и скринридера
            // сигнала нет вовсе ровно там, где он единственный.
            aria-label={staleSourceCount > 0 ? `Источники: устарели ${staleSourceCount}` : 'Источники'}>
            <span className="hidden sm:inline">Источники</span>
            {staleSourceCount > 0 && (
              <span className="ml-1 inline-flex items-center justify-center min-w-4 h-4 px-1 rounded-full
                bg-warning-subtle text-warning text-[10px] font-medium tabular-nums align-middle">
                {staleSourceCount}
              </span>
            )}
          </Button>
          {editable && (
            <div className="flex items-center gap-2 shrink-0">
              {savedFlash && <span className="text-sm text-success hidden sm:inline">Сохранено</span>}
              <Button variant="text" onClick={() => void doSave()} disabled={saving || uploading}
                title={uploading ? "Дождитесь загрузки изображения" : undefined}>
                {saving ? 'Сохранение…' : 'Сохранить'}
              </Button>
              <Button variant="filled" onClick={() => void doSaveAndClose()} loading={saving} disabled={uploading}
                title={uploading ? 'Дождитесь загрузки изображения' : 'Ctrl+Enter'}>
                Сохранить и закрыть
              </Button>
            </div>
          )}
        </div>
        <div role="tablist" aria-label="Разделы документа" className="flex border-b border-stroke gap-0 px-3 sm:px-4">
          {tabs.map(([key, label], i) => (
            <button key={key} role="tab" aria-selected={tab === key} tabIndex={tab === key ? 0 : -1}
              ref={el => { tabRefs.current[i] = el; }}
              onClick={() => requestTab(key)} onKeyDown={e => onTabKey(e, i)}
              className={`h-12 px-4 text-sm font-medium border-b-2 transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-brand ${
                tab === key ? 'border-brand text-brand' : 'border-transparent text-fg3 hover:text-fg1'}`}>
              {label}
              {key === 'requisites' && <BrokenCountBadge count={brokenTabCount} className="ml-1.5 align-middle" />}
              {key === tab && dirty && <span className="ml-1 text-warning" title="Есть несохранённые изменения">•</span>}
            </button>
          ))}
        </div>
      </div>
      {tab === 'requisites' && (
        <RequisitesTab instance={instance} setId={setId} schemaFields={schemaFields}
          allDocTypes={allDocTypes} docType={docType} otherInstances={otherInstances}
          onClose={onClose} onDirty={setDirty} saveRef={saveRef}
          onBaseState={setBaseState} baseControlRef={baseControlRef} />
      )}
      <Modal open={dataSourcesOpen} onOpenChange={setDataSourcesOpen} title="Источники данных" wide>
        {dataSourcesOpen && (
          <div className="space-y-3">
            <p className="text-xs text-fg4">
              Точечная привязка полей — по иконке источника у каждого поля в реквизитах.
              Здесь — обзор привязок, проверка данных и применение шаблонов на весь документ.
            </p>
            <DataSetsTab instance={instance} setId={setId} schemaFields={schemaFields} allDocTypes={allDocTypes} docType={docType} />
          </div>
        )}
      </Modal>
      {tab === 'quality' && (
        <div className="flex-1 min-h-0 overflow-y-auto px-6 py-4">
          <QualityLinksTab instance={instance} setId={setId} allDocTypes={allDocTypes} />
        </div>
      )}
      {tab === 'generation' && (
        <div className="flex-1 min-h-0 overflow-y-auto px-6 py-4">
          <GenerationTab instance={instance} setId={setId} schemaFieldKeys={schemaFields.map(f => f.key)} />
        </div>
      )}

      {pendingTab && (
        <Modal
          open
          onOpenChange={o => { if (!o && !switching) setPendingTab(null); }}
          title="Документ не сохранён"
          footer={
            <div className="flex gap-2 justify-end flex-wrap">
              <Button variant="text" size="sm" onClick={() => setPendingTab(null)} disabled={switching}>
                Отмена
              </Button>
              <Button variant="text" size="sm" danger onClick={() => switchTo(pendingTab)} disabled={switching}>
                Не сохранять
              </Button>
              <Button variant="filled" size="sm" onClick={saveThenSwitch} loading={switching}>
                {switching ? 'Сохранение...' : 'Сохранить'}
              </Button>
            </div>
          }>
          <p className="text-xs text-fg3">
            На текущей вкладке есть несохранённые изменения. Сохранить перед переходом?
            Иначе они будут потеряны.
          </p>
        </Modal>
      )}
    </div>
  );
}
