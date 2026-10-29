import { useState, useEffect, useRef, useMemo } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { ArrowBack, Edit } from '@/shared/ui/icons';
import { DocumentObservationsChip } from './DocumentObservations';
import { useRenameDocumentInstance, useResolutionDiagnostics, brokenRefPaths } from '@/shared/api/documentSets';
import { useListDataSetBindings } from '@/shared/api/datasets';
import { FUNCTIONAL_TAG } from '@/shared/api/tags';
import type { DocumentInstance, DocumentType } from '@/shared/api/types';
import { resolveEffectiveFields, compositeFieldHasTag } from '@/shared/api/schema';
import { STATUS_LABELS, STATUS_COLORS, BaseInstanceChip, ArchivedRefsProvider, type BaseCandidate } from '../fields';
import { ruCount } from '@/shared/utils/pluralize';
import { DataSetsTab } from './DataSetsTab';
import { QualityLinksTab } from './QualityLinksTab';
import { Modal, ModalExit } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { TabPanel, Tabs } from '@/shared/ui/Tabs';
import { containsRef } from './brokenRefs';
import { BrokenCountBadge } from './BrokenCountBadge';
import { RequisitesTab } from './RequisitesTab';
import { GenerationTab } from './GenerationTab';
import { useUploadsInFlight } from '@/shared/ui/uploadsInFlight';
import { useGuardedLeave, useLeaveGuard } from '@/shared/ui/NavigationGuard';
import { LeaveGuardDialog } from '@/shared/ui/LeaveGuardDialog';
import { Tooltip } from '@/shared/ui/Tooltip';

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
        onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); save(); } if (e.key === 'Escape') { e.stopPropagation(); setEditing(false); } }}
        autoFocus
        placeholder={docType?.name ?? 'Название документа'}
        className="text-sm font-medium text-on-surface bg-transparent border-b border-primary outline-none w-full min-w-0"
      />
    );
  }

  return (
    <button type="button" onClick={start} className="flex items-center gap-1.5 group/name text-left min-w-0 max-w-full">
      <span className={`text-sm font-medium truncate ${instance.name ? 'text-on-surface' : 'text-on-surface-variant'}`}>
        {instance.name || docType?.name || 'Документ'}
      </span>
      <Icon icon={Edit} size={18} className="text-on-surface-variant shrink-0 opacity-0 group-hover/name:opacity-100 transition-opacity" />
    </button>
  );
}

// ─── Instance editor modal ────────────────────────────────────────────────────

type InstanceTab = 'requisites' | 'quality' | 'generation' | 'sources';

/** Основа идентификаторов вкладок и панелей: редактор открыт на экране один. */
const TABS_ID = 'instance-editor';

export function InstanceEditor({ instance, setId, docType, allDocTypes, otherInstances, onClose, onDirtyChange }: {
  instance: DocumentInstance; setId: string; docType: DocumentType | undefined;
  allDocTypes: DocumentType[]; otherInstances: DocumentInstance[];
  /** Уйти из документа (к комплекту). Без вопроса о правках: его задаёт сам редактор. */
  onClose: () => void;
  onDirtyChange?: (dirty: boolean) => void;
}) {
  const schemaFields = docType ? resolveEffectiveFields(docType, allDocTypes) : [];
  const [tab, setTab] = useState<InstanceTab>('requisites');
  // Свод битых ссылок для бейджа на вкладке «Реквизиты» (issue #334). Общий кэш с индикаторами полей
  // (тот же queryKey) — без второго запроса; гейт по наличию ссылок в сохранённых реквизитах.
  const { data: editorDiagnostics } = useResolutionDiagnostics(instance.id, containsRef(instance.requisites));
  const brokenTabCount = useMemo(() => brokenRefPaths(editorDiagnostics).size, [editorDiagnostics]);
  // Устаревшие источники документа (issue #815) — счёт по ИСТОЧНИКАМ, а не по полям: действие
  // («Перераспознать») применяется к источнику, и «устарели источники: 2» отвечает на вопрос
  // «сколько раз мне это делать». Признак висит на вкладке «Источники данных» потому, что ряд вкладок
  // виден всегда: секция связанных полей свёрнута по умолчанию, а из разделов формы виден только открытый.
  const { data: docBindings = [] } = useListDataSetBindings({ ownerId: instance.id });
  const staleSourceCount = useMemo(
    () => new Set(docBindings.filter(b => b.source?.recognitionStale).map(b => b.sourceId)).size,
    [docBindings]);
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

  // «Грязное» состояние — наверх: страница закрепляет по нему снимок документа.
  useEffect(() => { onDirtyChange?.(dirty); }, [dirty, onDirtyChange]);

  /**
   * Уход из документа с правками — вопрос с «Сохранить и перейти» (issue #1384). Редактор — страница,
   * и спрашивает охрана страниц: стрелка «к комплекту», меню, палитра, перезагрузка. Сохранение
   * живёт здесь, поэтому и вопрос здесь, а не у страницы. «Назад» браузера охрана не видит
   * (`useGuardedLeave`) — как у формы счёта.
   */
  const [leave, setLeave] = useState<(() => void) | null>(null);
  useLeaveGuard(dirty, proceed => setLeave(() => proceed));
  const guardedLeave = useGuardedLeave();
  function proceedLeave() { const go = leave; setLeave(null); go?.(); }

  // Вкладка «Документы качества» — только для типов, требующих их (есть материал-массив
  // с полем-ссылкой на документ качества, тэг material.qualityDocLink).
  const requiresQuality = !!docType && compositeFieldHasTag(docType, FUNCTIONAL_TAG.materialQualityDocLink, allDocTypes);
  const tabs: [InstanceTab, string][] = [
    ['requisites', 'Реквизиты'],
    ...(requiresQuality ? [['quality', 'Документы качества'] as [InstanceTab, string]] : []),
    ['generation', 'Генерация'],
    ['sources', 'Источники данных'],
  ];

  function requestTab(next: InstanceTab) {
    if (next === tab) return;
    if (dirty) setPendingTab(next);   // есть несохранённые изменения — спрашиваем
    else setTab(next);
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
      {/* MD3 top app bar: «к комплекту» слева, имя+подзаголовок, статус, действия справа */}
      <div className="shrink-0 bg-surface">
        <div className="flex items-center gap-3 h-16 px-3 sm:px-4">
          <Tooltip content="К комплекту">
            <button type="button" onClick={() => guardedLeave(onClose)} aria-label="К комплекту"
              className="flex items-center justify-center w-11 h-11 shrink-0 rounded-full text-on-surface-variant hover:text-on-surface hover:bg-black/5 dark:hover:bg-white/10 transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary">
              <Icon icon={ArrowBack} size={20} />
            </button>
          </Tooltip>
          <div className="flex-1 min-w-0">
            <InstanceNameEditor instance={instance} setId={setId} docType={docType} />
            <p className="text-xs text-on-surface-variant mt-0.5 truncate">
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
          <span className={`text-xs px-2 py-0.5 rounded-full font-medium shrink-0 ${STATUS_COLORS[instance.status] ?? 'bg-primary-container text-primary'}`}>
            {STATUS_LABELS[instance.status] ?? instance.status}
          </span>
          {/* Замечания внешнего анализа, упоминающие ЭТОТ документ (issue #456). Находок сверки
              здесь нет: их связь с документом слабее, чем выглядит. */}
          <DocumentObservationsChip setId={instance.documentSetId} documentId={instance.id} />
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
        {/* APG-tablist (issue #107 F3) с ручной активацией — стрелки двигают фокус, не переключают:
            уход с вкладки спрашивает про несохранённое (requestTab), и стрелка его вызывать не должна. */}
        <Tabs id={TABS_ID} aria-label="Разделы документа" className="px-3 sm:px-4" value={tab} onChange={requestTab}
          items={tabs.map(([key, label]) => ({
            value: key, label,
            title: key === 'sources' && staleSourceCount > 0
              ? `${ruCount(staleSourceCount, 'источник устарел', 'источника устарели', 'источников устарели')}: данные от прежнего файла. Откройте, чтобы перераспознать.`
              : undefined,
            badge: <>
              {key === 'requisites' && <BrokenCountBadge count={brokenTabCount} className="align-middle" />}
              {/* Счётчик — в доступном имени вкладки, а не только в title: тот виден лишь мышью. */}
              {key === 'sources' && staleSourceCount > 0 && (
                <span className="inline-flex items-center justify-center min-w-4 h-4 px-1 rounded-full
                  bg-warning-container text-on-warning-container text-caption font-medium tabular-nums">
                  <span className="sr-only">устарели: </span>{staleSourceCount}
                </span>
              )}
              {key === tab && dirty && <Tooltip content="Есть несохранённые изменения"><span className="text-warning">•</span></Tooltip>}
            </>,
          }))} />
      </div>
      {tab === 'requisites' && (
        // Пометки «в архиве» у плиток ссылок — по сохранённым реквизитам документа (issue #1185).
        <TabPanel id={TABS_ID} value="requisites" className="flex flex-col min-h-0 flex-1">
          <ArchivedRefsProvider data={instance.requisites} ownerId={instance.id}>
            <RequisitesTab instance={instance} setId={setId} schemaFields={schemaFields}
              allDocTypes={allDocTypes} docType={docType} otherInstances={otherInstances}
              onClose={onClose} onDirty={setDirty} saveRef={saveRef}
              onBaseState={setBaseState} baseControlRef={baseControlRef} />
          </ArchivedRefsProvider>
        </TabPanel>
      )}
      {tab === 'quality' && (
        <TabPanel id={TABS_ID} value="quality" className="flex-1 min-h-0 overflow-y-auto px-6 py-4">
          <QualityLinksTab instance={instance} setId={setId} allDocTypes={allDocTypes} />
        </TabPanel>
      )}
      {tab === 'generation' && (
        <TabPanel id={TABS_ID} value="generation" className="flex-1 min-h-0 overflow-y-auto px-6 py-4">
          <GenerationTab instance={instance} setId={setId} schemaFieldKeys={schemaFields.map(f => f.key)} />
        </TabPanel>
      )}
      {/* Источники данных (issue #296, фаза 3) — пакетные операции уровня документа: обзор привязок,
          «Проверить данные», «Из шаблона». Точечная привязка — на самих полях. Вкладкой, а не окном
          (issue #1384): окно стояло бы над страницей, а его «Из шаблона» и распознавание — над ним. */}
      {tab === 'sources' && (
        <TabPanel id={TABS_ID} value="sources" className="flex-1 min-h-0 overflow-y-auto px-6 py-4 space-y-3">
          <p className="text-xs text-on-surface-variant">
            Точечная привязка полей — по иконке источника у каждого поля в реквизитах.
            Здесь — обзор привязок, проверка данных и применение шаблонов на весь документ.
          </p>
          <DataSetsTab instance={instance} setId={setId} schemaFields={schemaFields} allDocTypes={allDocTypes} docType={docType} />
        </TabPanel>
      )}

      {pendingTab && (
        <Modal
          open
          onOpenChange={o => { if (!o) setPendingTab(null); }}
          title="Документ не сохранён"
          // Вопрос: промах и Esc равны «Отмене» — безопасному ответу. Запрос сильнее.
          nothingToLose busy={switching}
          footer={
            <div className="flex gap-2 justify-end flex-wrap">
              <ModalExit size="sm" />
              <Button variant="text" size="sm" danger onClick={() => switchTo(pendingTab)} disabled={switching}>
                Не сохранять
              </Button>
              <Button variant="filled" size="sm" onClick={saveThenSwitch} loading={switching}>
                {switching ? 'Сохранение...' : 'Сохранить'}
              </Button>
            </div>
          }>
          <p className="text-xs text-on-surface-variant">
            На текущей вкладке есть несохранённые изменения. Сохранить перед переходом?
            Иначе они будут потеряны.
          </p>
        </Modal>
      )}
      {/* Пока картинка едет, сохранять рано — как у кнопок и Ctrl+Enter (issue #532, ревью PR #1388). */}
      <LeaveGuardDialog open={!!leave} saving={saving}
        saveBlocked={uploading ? 'Дождитесь загрузки изображения' : undefined}
        onSave={async () => { if (uploading) return; if (await doSave()) proceedLeave(); else setLeave(null); }}
        onDiscard={proceedLeave} onCancel={() => setLeave(null)} />
    </div>
  );
}
