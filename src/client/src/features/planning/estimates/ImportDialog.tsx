import { useRef, useState, type DragEvent } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { CircularProgress } from '@/shared/ui/CircularProgress';
import { UploadFile } from '@/shared/ui/icons';
import { useConstructionDirectory, useGetConstruction } from '@/shared/api/constructions';
import { planRefusal, useConstructionEstimates, type EstimateKind } from '@/shared/api/planningEstimates';
import {
  useCancelImport, useCommitImport, useEstimateImport, useStartImport, type EstimateImport, type ImportRequest,
} from '@/shared/api/planningImports';
import { Button } from '@/shared/ui/Button';
import { carriesFiles, droppedFiles } from '@/shared/ui/fileDrop';
import { Modal, ModalExit } from '@/shared/ui/Modal';
import { Select, SelectItem } from '@/shared/ui/Select';
import { RevealItem, SiteSelectItems } from '@/shared/siteFilter/SiteOptions';
import { pickSite, REVEAL_CHOICE, useSiteChoices } from '@/shared/siteFilter/siteReveal';
import { TabPanel, Tabs } from '@/shared/ui/Tabs';
import { TextField } from '@/shared/ui/TextField';
import { useSnackbar } from '@/shared/ui/Snackbar';
import { WindowStep } from '@/shared/ui/WindowStep';
import { formatBytes, formatCount } from '@/shared/format/format';
import { ruPlural } from '@/shared/utils/pluralize';
import { KIND_LABEL } from './estimateRows';
import { importVerdict, partialCommitLabel, withoutRows, type ImportCreated } from './importReport';
import { ParseReport } from './ParseReport';

interface ImportDialogProps {
  open: boolean;
  onClose: () => void;
  /** Стройка; не названа — выбирается в окне (общий раздел «Сметы»). */
  constructionId?: string;
  /** Окно открыто из карточки сметы: цель — новая редакция этой сметы, выбора нет. */
  estimateId?: string;
  /** Раздел стройки — у новой сметы по умолчанию (страница раздела). */
  sectionId?: string;
  /** Импорт, закрытый без решения: окно открывается на его отчёте («ждёт решения» над сметами). */
  importId?: string;
  onCreated: (created: ImportCreated) => void;
}

/**
 * Окно загрузки сметы (change `plan-09-estimate-ui`, PR 2, ТЗ PLAN-6, PLAN-7, макет П1 1в): шаги «Файл и
 * цель → Разбор → Отчёт» одного окна `Modal wide`.
 *
 * - Цель выбирает человек (решение В-12 (а)): новая редакция сметы X или новая смета. Без выбора загрузки нет.
 * - Разбор идёт в фоне: окно можно закрыть, импорт останется полосой «ждёт решения» над сметами стройки.
 * - Редакция создаётся только действием человека; при не разобранных позициях — кнопкой, называющей
 *   последствие, и подтверждением шагом окна (`acceptPartial`). Отказ — «Отказаться»: импорт и файл уходят,
 *   поэтому тоже через подтверждение шагом окна — кнопка стоит рядом с главной.
 * - Не ЛСР (ВОР, КА) редакцией не становится (решение В-7): «Создать редакцию» нет.
 */
export function ImportDialog(props: ImportDialogProps) {
  // Состояние окна живёт, пока оно открыто: закрытое и открытое снова начинает с начала.
  return props.open ? <ImportWindow {...props} /> : null;
}

type TargetMode = 'existing' | 'new';

function ImportWindow({ onClose, constructionId, estimateId, sectionId, importId: reopened, onCreated }: ImportDialogProps) {
  const [importId, setImportId] = useState<string | null>(reopened ?? null);
  const [site, setSite] = useState(constructionId ?? '');
  const [file, setFile] = useState<File | null>(null);
  const [mode, setMode] = useState<TargetMode>('existing');
  const [target, setTarget] = useState(estimateId ?? '');
  const [name, setName] = useState('');
  const [kind, setKind] = useState<EstimateKind>('main');
  const [section, setSection] = useState(sectionId ?? '');
  const [confirming, setConfirming] = useState(false);
  const [refusing, setRefusing] = useState(false);

  const toast = useSnackbar();
  const start = useStartImport();
  const commit = useCommitImport();
  const cancel = useCancelImport();
  const report = useEstimateImport(importId);
  const estimates = useConstructionEstimates(site);
  const construction = useGetConstruction(site);
  const directory = useConstructionDirectory(!constructionId);
  // Стройки — с «Избранными» первыми (#1432), остальные — выходом «Показать все стройки», как в любом выборе стройки.
  const sites = useSiteChoices(directory.data, site || null);

  const sections = construction.data?.sections ?? [];
  const sectionName = (id: string) => sections.find(s => s.id === id)?.name;
  const known = estimates.data?.items ?? [];
  // Пока сметы стройки не прочитаны, «смет нет» не известно: цель не выбирается, а не предлагается «новая смета».
  const listed = estimates.data !== undefined;
  // Смет у стройки нет — выбирать не из чего: только новая.
  const shownMode: TargetMode = estimateId ? 'existing' : known.length === 0 ? 'new' : mode;
  const fixed = estimateId ? known.find(e => e.id === estimateId) : undefined;

  const busy = start.isPending || commit.isPending || cancel.isPending;
  const failure = start.error ?? commit.error ?? cancel.error;
  // Отчёт не прочитался (импорт тем временем убрали) — показываем отказ, а не прежний отчёт и не идущий разбор.
  const data = report.isError ? undefined : report.data;
  const verdict = data ? importVerdict(data) : null;
  const step: 'pick' | 'parsing' | 'report' =
    importId === null ? 'pick' : report.isError ? 'report' : !data || verdict?.kind === 'parsing' ? 'parsing' : 'report';

  const request = (): ImportRequest | string => {
    if (!site) return 'Выберите стройку';
    if (!file) return 'Выберите файл сметы';
    if (!estimateId && !listed) return estimates.isError ? 'Сметы стройки не прочитались' : 'Читаем сметы стройки…';
    if (shownMode === 'existing') return target ? { file, constructionId: site, estimateId: target } : 'Выберите смету';
    return name.trim()
      ? { file, constructionId: site, newEstimate: { name: name.trim(), kind, sectionId: section || null } }
      : 'Назовите новую смету';
  };
  const ready = request();

  const upload = () => {
    if (typeof ready === 'string') return;
    start.mutate(ready, { onSuccess: started => setImportId(started.id) });
  };
  const refuse = (r: EstimateImport) =>
    cancel.mutate({ id: r.id, version: r.version, constructionId: r.constructionId },
      { onSuccess: onClose, onSettled: () => setRefusing(false) });
  const create = (r: EstimateImport, acceptPartial: boolean) =>
    commit.mutate({ id: r.id, version: r.version, acceptPartial }, {
      onSuccess: done => {
        const revision = done.estimate.revisions.find(x => x.id === done.revisionId);
        // Первая редакция сметы становится действующей сама (решение В-5), следующие — действием в карточке.
        // Повтор — редакция создана раньше тем же импортом (другой вкладкой): ответ называет её, второй нет.
        toast.success(done.repeated
          ? `Ред. ${done.revisionNumber} сметы «${done.estimate.name}» уже создана из этого файла`
          : `Ред. ${done.revisionNumber} сметы «${done.estimate.name}» создана${revision?.current ? ' и стала действующей' : ''}`);
        onCreated({ estimateId: done.estimate.id, revisionId: done.revisionId, unmatched: (revision?.unmatched ?? 0) > 0 });
        onClose();
      },
      onSettled: () => setConfirming(false),
    });

  const footer = (
    <div className="flex flex-wrap justify-end gap-2">
      {step === 'pick' ? (
        <>
          <ModalExit />
          <Button variant="filled" disabled={typeof ready === 'string' || busy} title={typeof ready === 'string' ? ready : undefined}
            loading={start.isPending} onClick={upload}>Загрузить</Button>
        </>
      ) : step === 'parsing' ? (
        <>
          {data && <Button variant="text" disabled={busy} onClick={() => setRefusing(true)}>Отказаться</Button>}
          <ModalExit variant="outlined">Закрыть</ModalExit>
        </>
      ) : verdict?.kind === 'ready' && data ? (
        // Закрыть без решения — крестиком: импорт останется полосой «ждёт решения» над сметами.
        <>
          <Button variant="text" disabled={busy} onClick={() => setRefusing(true)}>Отказаться</Button>
          {verdict.unparsed > 0
            ? <Button variant="filled" disabled={busy} onClick={() => setConfirming(true)}>{partialCommitLabel(verdict.unparsed)}</Button>
            : <Button variant="filled" disabled={busy} loading={commit.isPending} onClick={() => create(data, false)}>Создать редакцию</Button>}
        </>
      ) : (
        <>
          <ModalExit>Закрыть</ModalExit>
          {data && verdict?.kind !== 'committed' && (
            <Button variant="tonal" disabled={busy} onClick={() => setRefusing(true)}>Убрать импорт</Button>
          )}
        </>
      )}
    </div>
  );

  const targetFields = shownMode === 'existing' ? (
    <Select label="Смета" value={target || undefined} emptyItem="— не выбрана —" onValueChange={setTarget}>
      {known.map(e => <SelectItem key={e.id} value={e.id}>{e.name} · {KIND_LABEL[e.kind]}</SelectItem>)}
    </Select>
  ) : (
    <div className="grid gap-3 sm:grid-cols-2">
      <TextField label="Название сметы" value={name} onChange={e => setName(e.target.value)} containerClassName="sm:col-span-2" />
      <Select label="Вид" value={kind} onValueChange={v => setKind(v as EstimateKind)}>
        <SelectItem value="main">{KIND_LABEL.main}</SelectItem>
        <SelectItem value="additional">{KIND_LABEL.additional}</SelectItem>
      </Select>
      {sections.length > 0 && (
        <Select label="Раздел стройки" value={section || undefined} emptyItem="вся стройка" onValueChange={setSection}>
          {sections.map(s => <SelectItem key={s.id} value={s.id}>{s.name}</SelectItem>)}
        </Select>
      )}
    </div>
  );

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title="Загрузка сметы" wide busy={busy}
      isDirty={step === 'pick' && (file !== null || name.trim() !== '')} footer={footer}>
      <div className="space-y-4">
        <Steps step={step} />
        {step === 'pick' && (
          <div className="space-y-4">
            {!constructionId && (
              <Select label="Стройка" value={site || undefined} emptyItem="— не выбрана —" stayOpenOn={REVEAL_CHOICE}
                onValueChange={v => pickSite(v, sites.reveal, next => { setSite(next); setTarget(''); setSection(''); })}>
                <SiteSelectItems choices={sites} />
                <RevealItem hidden={sites.hidden} />
              </Select>
            )}
            <FilePick file={file} onFile={setFile} />
            {site && (estimateId ? (
              <p className="text-body-medium text-on-surface-variant">Куда: <span className="text-on-surface">новая редакция сметы «{fixed?.name ?? '…'}»</span></p>
            ) : estimates.isError ? (
              <p role="alert" className="text-body-medium text-error">{planRefusal(estimates.error).text}</p>
            ) : !listed ? (
              <p className="text-body-medium text-on-surface-variant">Читаем сметы стройки…</p>
            ) : (
              <fieldset className="space-y-3">
                <legend className="text-body-medium font-medium text-on-surface mb-2">Куда ляжет редакция</legend>
                {known.length > 0 && (
                  <Tabs id="import-target" variant="segmented" aria-label="Куда ляжет редакция" value={shownMode} onChange={setMode}
                    items={[{ value: 'existing', label: 'Новая редакция сметы' }, { value: 'new', label: 'Новая смета' }]} />
                )}
                {known.length > 0 ? <TabPanel id="import-target" value={shownMode}>{targetFields}</TabPanel> : targetFields}
              </fieldset>
            ))}
          </div>
        )}
        {step === 'parsing' && (
          <div className="flex items-start gap-3 text-body-medium">
            <CircularProgress size={18} className="mt-0.5 shrink-0 text-primary" />
            <div role="status" className="space-y-1">
              <p className="text-on-surface">Разбираем {data ? <b className="font-medium">{data.fileName}</b> : 'файл'}{data?.progress ? ` — ${data.progress}` : '…'}</p>
              <p className="text-on-surface-variant">Разбор идёт в фоне: окно можно закрыть — импорт останется над сметами стройки, и отчёт откроется оттуда.</p>
            </div>
          </div>
        )}
        {step === 'report' && data && <ParseReport report={data} sectionName={sectionName} />}
        {step === 'report' && verdict?.kind === 'committed' && (
          <p className="text-body-medium text-on-surface-variant">Редакция из этого файла уже создана — она в списке редакций сметы.</p>
        )}
        {report.isError && <p role="alert" className="text-body-medium text-error">{planRefusal(report.error).text}</p>}
        {failure && <p role="alert" className="text-body-medium text-error">{planRefusal(failure).text}</p>}
      </div>
      {confirming && data && verdict?.kind === 'ready' && (
        <WindowStep title={`Создать редакцию ${withoutRows(verdict.unparsed)}?`}
          onBack={() => setConfirming(false)} busy={commit.isPending}
          footer={<div className="flex flex-wrap justify-end gap-2">
            <ModalExit>Назад</ModalExit>
            <Button variant="filled" loading={commit.isPending} onClick={() => create(data, true)}>Создать редакцию</Button>
          </div>}>
          <p className="text-body-medium text-on-surface-variant">
            В редакцию войдут {formatCount(data.parsed ?? 0)} из {formatCount(data.inFile ?? 0)} позиций файла.{' '}
            {formatCount(verdict.unparsed)} не {ruPlural(verdict.unparsed, 'разобранная', 'разобранные', 'разобранных')} в неё не{' '}
            {ruPlural(verdict.unparsed, 'войдёт', 'войдут', 'войдут')}: добавить их можно только новой загрузкой исправленного файла.
            У редакции останется пометка «частично».
          </p>
        </WindowStep>
      )}
      {refusing && data && (
        <WindowStep title={`Отказаться от импорта «${data.fileName}»?`} onBack={() => setRefusing(false)} busy={cancel.isPending}
          footer={<div className="flex flex-wrap justify-end gap-2">
            <ModalExit>Назад</ModalExit>
            <Button variant="filled" danger loading={cancel.isPending} onClick={() => refuse(data)}>Отказаться от импорта</Button>
          </div>}>
          <p className="text-body-medium text-on-surface-variant">
            Импорт и загруженный файл удалятся, редакции из него не будет. Вернуться к нему можно только новой загрузкой
            файла.
          </p>
        </WindowStep>
      )}
    </Modal>
  );
}

const STEPS = [['pick', 'Файл и цель'], ['parsing', 'Разбор'], ['report', 'Отчёт']] as const;

function Steps({ step }: { step: (typeof STEPS)[number][0] }) {
  return (
    <ol className="flex flex-wrap items-center gap-x-2 gap-y-1 text-body-small text-on-surface-variant" aria-label="Шаги загрузки">
      {STEPS.map(([key, label], i) => (
        <li key={key} aria-current={key === step ? 'step' : undefined} className={key === step ? 'font-medium text-primary' : undefined}>
          {i > 0 && <span aria-hidden className="mr-2 text-on-surface-variant">›</span>}{i + 1} {label}
        </li>
      ))}
    </ol>
  );
}

/** Файлы ГРАНД-Сметы: OpenXML — `.gsfx` (архив) или голый `.xml`. Вид документа сервер узнаёт по содержимому. */
const ACCEPT = '.gsfx,.xml';

/** Место файла: перетащить или выбрать. Один файл — одна редакция. */
function FilePick({ file, onFile }: { file: File | null; onFile: (file: File) => void }) {
  const input = useRef<HTMLInputElement>(null);
  const [over, setOver] = useState(false);
  const [several, setSeveral] = useState(false);

  const onDragOver = (e: DragEvent) => {
    if (!carriesFiles(e.dataTransfer)) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'copy';
    setOver(true);
  };
  const onDrop = (e: DragEvent) => {
    setOver(false);
    if (!carriesFiles(e.dataTransfer)) return;
    e.preventDefault();
    const { files } = droppedFiles(e.dataTransfer);
    setSeveral(files.length > 1);
    if (files.length === 1) onFile(files[0]);
  };

  return (
    <div onDragOver={onDragOver} onDragLeave={e => { if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setOver(false); }}
      onDrop={onDrop}
      className={`flex flex-wrap items-center gap-3 rounded-md border-2 border-dashed px-4 py-3 text-body-medium
        ${over ? 'border-primary bg-primary-container' : 'border-outline-variant'}`}>
      <Icon icon={UploadFile} size={20} className="shrink-0 text-on-surface-variant" aria-hidden />
      <div className="min-w-0 flex-1">
        {file
          ? <p className="text-on-surface break-all">{file.name} <span className="text-on-surface-variant">· {formatBytes(file.size)}</span></p>
          : <p className="text-on-surface-variant">Перетащите сюда файл сметы из ГРАНД-Сметы (.gsfx или .xml)</p>}
        {several && <p className="text-body-small text-error">Одна загрузка — один файл: перетащите их по одному.</p>}
      </div>
      <Button variant="outlined" onClick={() => input.current?.click()}>{file ? 'Другой файл' : 'Выбрать файл'}</Button>
      <input ref={input} type="file" className="hidden" accept={ACCEPT} aria-label="Файл сметы"
        onChange={e => {
          const picked = e.target.files?.[0];
          e.target.value = '';
          if (picked) { setSeveral(false); onFile(picked); }
        }} />
    </div>
  );
}
