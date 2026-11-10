import { useEffect, useState } from 'react';
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router';
import { Menu, MenuItem } from '@/shared/ui/Menu';
import { Icon } from '@/shared/ui/Icon';
import { ChevronLeft, LockIcon, Upload } from '@/shared/ui/icons';
import { costHidden, useEstimate, type Estimate, type EstimateRevision } from '@/shared/api/planningEstimates';
import { Button } from '@/shared/ui/Button';
import { Chip } from '@/shared/ui/Chip';
import { CountText } from '@/shared/ui/CountText';
import { SegmentedButton } from '@/shared/ui/SegmentedButton';
import { StatusText } from '@/shared/ui/StatusText';
import { TabPanel, Tabs } from '@/shared/ui/Tabs';
import { apiError } from '@/shared/utils/apiError';
import { formatCount, formatDate, formatMoney } from '@/shared/format/format';
import { attachmentKey } from './attachmentRows';
import { EstimateAttachments } from './EstimateAttachments';
import { KIND_LABEL, latestRevision, visibleRevisions } from './estimateRows';
import { ImportDialog } from './ImportDialog';
import { openingOf, type RevisionOpening } from './importReport';
import { MakeCurrentDialog } from './MakeCurrentDialog';
import { PendingImports } from './PendingImports';
import { RevisionDistribution } from '../distribution/RevisionDistribution';
import { RevisionLines } from './RevisionLines';
import { useAttachmentsOf } from './useAttachmentsOf';
import { useEstimateEdit } from './useEstimateEdit';

/**
 * Карточка сметы (change `plan-09-estimate-ui`, макет П1, вариант 1): на месте, «‹ Сметы» — назад. Редакции —
 * вкладками: видны действующая и последние, остальные — в «ещё N» (у живой сметы их 5–10). Под вкладками —
 * полоса редакции и её строки.
 *
 * Права `read`, `edit`, `cost` независимы, недоступное скрыто (П1, §75 E): без `plan.estimate.edit` нет «Сделать
 * действующей» и «Новой редакции». На телефоне — только чтение (решение П1): правок нет и там.
 *
 * Пришли сюда созданием редакции (окно загрузки, `RevisionOpening` в состоянии перехода) — открыта она, и с
 * отбором «не сопоставлено», если оно есть. Состояние перехода снимается сразу: «Назад» и перезагрузка
 * возвращают к карточке, а не к той редакции с тем отбором.
 *
 * Над строками — режим «Сопоставление | Распределение» (`plan-10`, макет П2): распределение по разделам стройки.
 * У сметы на разделе стройки режима нет — её строки целиком в том разделе (ответ листа 0 П2).
 *
 * Над всем этим — разделы верхнего уровня «Редакции N | Приложения N» (`plan-11`, макет П11, вариант 1), primary-вид
 * `Tabs` ([UI-90]); ряд редакций — `underline` в его панели ([UI-91]). Раздел — в адресе: `?view=attachments`, выбранное
 * приложение — `?attachment=ka:<id>`; редакции — без параметра. Приложенный файл (из «Приложить файл…» или КА, брошенный
 * в «Новую редакцию») открывается в «Приложениях»; редакция, созданная из «Приложить файл…», — в «Редакциях».
 */
export function EstimateCard({ estimateId, back }: { estimateId: string; back: { to: string; label: string } }) {
  const estimate = useEstimate(estimateId);
  const location = useLocation();
  const navigate = useNavigate();
  const [opening, setOpening] = useState<RevisionOpening | null>((location.state as RevisionOpening | null) ?? null);
  const [chosen, setChosen] = useState<string | null>(opening?.revisionId ?? null);
  const editable = useEstimateEdit();
  const arrived = location.state !== null && location.state !== undefined;
  useEffect(() => {
    if (arrived) void navigate({ pathname: location.pathname, search: location.search, hash: location.hash }, { replace: true, state: null });
  }, [arrived, navigate, location.pathname, location.search, location.hash]);
  const [importing, setImporting] = useState<{ importId?: string; attach?: boolean } | null>(null);
  const [mode, setMode] = useState<Mode>(opening?.mode ?? 'match');
  const [params, setParams] = useSearchParams();
  const view: View = params.get('view') === 'attachments' ? 'attachments' : 'revisions';
  const attachments = useAttachmentsOf(estimateId);
  // Раздел — шаг истории («Назад» возвращает к редакциям), выбор приложения — нет: листая список, назад не копят.
  const showView = (next: View, attachment?: string) => setParams(prev => {
    const p = new URLSearchParams(prev);
    p.delete('attachment');
    if (next === 'attachments') { p.set('view', 'attachments'); if (attachment) p.set('attachment', attachment); }
    else p.delete('view');
    return p;
  });
  const pick = (key: string | null) => setParams(prev => {
    const p = new URLSearchParams(prev);
    if (key) p.set('attachment', key); else p.delete('attachment');
    return p;
  }, { replace: true });

  if (estimate.isError) return (
    <div className="space-y-3">
      <BackLink back={back} />
      <p role="alert" className="text-body-medium text-error">{apiError(estimate.error, 'Смета не прочиталась')}</p>
    </div>
  );
  if (!estimate.data) return <p className="text-body-medium text-on-surface-variant">Читаем смету…</p>;

  const data = estimate.data;
  const shown = data.revisions.find(r => r.id === chosen) ?? latestRevision(data);
  const viewId = `estimate-${data.id}-view`;
  const revisionsId = `estimate-${data.id}`;
  return (
    <div className="space-y-3">
      <BackLink back={back} />
      <div className="flex flex-wrap items-center gap-2">
        <h2 className="text-title-medium text-on-surface min-w-0 truncate">{data.name}</h2>
        <StatusText tone="neutral">{KIND_LABEL[data.kind]}</StatusText>
        {editable && (
          <Button variant="outlined" className="ml-auto" icon={<Icon icon={Upload} size={18} aria-hidden />} onClick={() => setImporting({})}>Новая редакция</Button>
        )}
      </div>
      <PendingImports constructionId={data.constructionId} enabled={editable} only={i => i.target.estimateId === data.id}
        onOpen={id => setImporting({ importId: id })} />
      <ImportDialog open={importing !== null} onClose={() => setImporting(null)} constructionId={data.constructionId}
        estimateId={data.id} importId={importing?.importId} attach={importing?.attach}
        onCreated={created => {
          const next = openingOf(created); setOpening(next); setChosen(next.revisionId); setMode(next.mode ?? 'match');
          if (view !== 'revisions') showView('revisions');
        }}
        onAttached={a => showView('attachments', attachmentKey(a))} />

      <Tabs id={viewId} variant="primary" aria-label="Разделы сметы" value={view} onChange={next => showView(next)}
        items={[
          { value: 'revisions', label: 'Редакции', badge: <Count n={data.revisions.length} /> },
          { value: 'attachments', label: 'Приложения', badge: attachments.count !== null ? <Count n={attachments.count} /> : undefined },
        ]} />
      <TabPanel id={viewId} value={view} className="space-y-3">
        {view === 'attachments' ? (
          <EstimateAttachments estimateId={data.id} selected={params.get('attachment')} onSelect={pick}
            attach={() => setImporting({ attach: true })} />
        ) : shown === null ? (
          <p className="text-body-medium text-on-surface-variant">Редакций у сметы нет — их создаёт загрузка файла ЛСР.</p>
        ) : (
          <>
            <RevisionTabs id={revisionsId} estimate={data} selected={shown.id} onSelect={setChosen} />
            <TabPanel id={revisionsId} value={shown.id} className="space-y-3">
              <RevisionBand estimate={data} revision={shown} />
              {data.sectionId === null && (
                <SegmentedButton aria-label="Режим строк редакции" value={mode} onChange={setMode}
                  items={[{ value: 'match', label: 'Сопоставление' }, { value: 'distribution', label: 'Распределение' }]} />
              )}
              {mode === 'distribution' && data.sectionId === null ? (
                <RevisionDistribution key={shown.id} revision={shown} constructionId={data.constructionId}
                  initialQueue={opening?.revisionId === shown.id && opening.mode === 'distribution' ? 'carry-over' : 'all'} />
              ) : (
                <RevisionLines key={shown.id} revision={shown} initialQueue={opening?.revisionId === shown.id ? opening.queue : 'all'} />
              )}
            </TabPanel>
          </>
        )}
      </TabPanel>
    </div>
  );
}

type Mode = 'match' | 'distribution';
type View = 'revisions' | 'attachments';

/**
 * Счётчик вкладки раздела. Пробел перед числом — для диктора: без него имя вкладки «Приложения3» (`Tabs` ставит счётчик
 * вплотную к подписи, зазор у них — отступом ряда). В ряду flex пробельный текст не рисуется, вид не меняется.
 */
function Count({ n }: { n: number }) {
  return <>{' '}<CountText>{formatCount(n)}</CountText></>;
}

function BackLink({ back }: { back: { to: string; label: string } }) {
  return (
    <Link to={back.to} className="inline-flex items-center gap-1 text-body-medium text-primary hover:underline">
      <Icon icon={ChevronLeft} size={18} aria-hidden /> {back.label}
    </Link>
  );
}

const revisionLabel = (r: EstimateRevision) =>
  `Ред. ${r.number}${r.current ? ' · действующая' : r.frozen ? ` · ${r.frozen.label}` : ''}`;

function RevisionTabs({ id, estimate, selected, onSelect }: {
  id: string; estimate: Estimate; selected: string; onSelect: (id: string) => void;
}) {
  const { tabs, more } = visibleRevisions(estimate, selected);
  return (
    <div className="flex items-center gap-2">
      <Tabs id={id} aria-label="Редакции сметы" className="flex-1 min-w-0"
        items={tabs.map(r => ({ value: r.id, label: revisionLabel(r) }))} value={selected} onChange={onSelect} />
      {more.length > 0 && (
        <Menu trigger={<Chip selected={false} menu className="shrink-0">ещё {formatCount(more.length)}</Chip>}>
          {more.map(r => (
            <MenuItem key={r.id} onSelect={() => onSelect(r.id)} trailing={formatDate(r.importedAt)}>
              {revisionLabel(r)}
            </MenuItem>
          ))}
        </Menu>
      )}
    </div>
  );
}

/**
 * Полоса редакции: откуда она, сколько строк, действующая ли и чем зафиксирована. «Не действующая» — тоном
 * neutral, а не warning (§75 E): это не ошибка, а состояние, у живой сметы таких редакций большинство.
 */
function RevisionBand({ estimate, revision }: { estimate: Estimate; revision: EstimateRevision }) {
  const [asked, setAsked] = useState<{ id: string; number: number } | null>(null);
  const editable = useEstimateEdit();
  const closed = costHidden(estimate.hidden);
  const current = estimate.revisions.find(r => r.current) ?? null;

  return (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-2 rounded-sm bg-surface-container px-3 py-2 text-body-medium text-on-surface-variant">
      <span>
        {formatDate(revision.importedAt)}
        {revision.importedBy && ` · ${revision.importedBy}`}
        {revision.sourceFileName && ` · ${revision.sourceFileName}`}
        {` · ${formatCount(revision.rows)} строк`}
      </span>
      {revision.current
        ? <StatusText tone="primary">действующая</StatusText>
        : (
          <StatusText tone="neutral" title={current ? `План стройки считается по ред. ${current.number}` : 'Действующей редакции у сметы нет'}>
            не действующая
          </StatusText>
        )}
      {revision.frozen && (
        <StatusText tone="neutral" icon={LockIcon} title={`Зафиксирована ${formatDate(revision.frozen.at)}: из неё сделано «${revision.frozen.label}». Строки не правятся.`}>
          зафиксирована: {revision.frozen.label}
        </StatusText>
      )}
      {revision.partial && (
        <StatusText tone="warning" title="Редакция создана из неполного разбора файла: часть позиций не вошла">
          частично {formatCount(revision.partial.parsed)} из {formatCount(revision.partial.inFile)}
        </StatusText>
      )}
      {estimate.sectionId === null && <DistributedText counts={revision.distribution} />}
      {/* Итог — только с правом: без него суммы нет вовсе, причину называет колонка стоимости таблицы. */}
      {!closed && revision.cost && <span>итог <span className="font-medium text-on-surface">{formatMoney(revision.cost.total)}</span></span>}

      {editable && !revision.current && (
        <Button variant="tonal" className="ml-auto" onClick={() => setAsked({ id: revision.id, number: revision.number })}>
          Сделать действующей
        </Button>
      )}
      <MakeCurrentDialog estimateId={estimate.id} revision={asked} onClose={() => setAsked(null)} />
    </div>
  );
}

/**
 * «Распределено N из M» в полосе редакции — всегда, в любом режиме (ответ листа 0 П2). «Стройка в целом» — явный
 * выбор и в «не распределено» не входит; частично распределённые — отдельно.
 */
function DistributedText({ counts }: { counts: EstimateRevision['distribution'] }) {
  const done = counts.distributed + counts.whole;
  return (
    <StatusText tone="neutral">
      распределено {formatCount(done)} из {formatCount(counts.rows)}
      {counts.partial > 0 ? ` · частично ${formatCount(counts.partial)}` : ''}
      {counts.carryOverPending > 0 ? ` · перенос: проверить ${formatCount(counts.carryOverPending)}` : ''}
    </StatusText>
  );
}
