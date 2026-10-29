import { formatDayTime } from '@/shared/format/format';
import { useMemo, useState } from 'react';
import { Link } from 'react-router';
import { Icon } from '@/shared/ui/Icon';
import { BugReport, Check, ChevronRight, Close, ContentCopy, ImageIcon, KeyboardArrowDown, OpenInNew, Send, Undo, Warning } from '@/shared/ui/icons';
import { Button } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { Modal } from '@/shared/ui/Modal';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { apiError } from '@/shared/utils/apiError';
import { ListDetailShell, NavSearchInput, NavSection } from '@/shared/ui/ListDetailShell';
import { useListDetailView } from '@/shared/ui/listDetailView';
import { useToast } from '@/shared/ui/Toast';
import { StatusBadge, type StatusTone } from '@/shared/ui/StatusBadge';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { useOpenAttachment } from '@/shared/hooks/useOpenAttachment';
import {
  useBugReports, useBugReport, useSaveBugReportDraft, useMarkBugReportFixed,
  useRejectBugReport, useReopenBugReport, useForwardBugReport,
  type BugReportDetail, type BugReportListItem, type BugReportStatus, type BugReportTech,
} from '@/shared/api/bugReports';

/**
 * Разбор сообщений об ошибках (issue #834).
 *
 * Экран администратора и есть тот фильтр, ради которого сообщения не уходят в GitHub напрямую:
 * репозиторий публичный, а пользователь описывает беду названиями строек и организаций. Здесь текст
 * правят ДО публикации — потому поле «Текст issue» редактируемое, а не показ «как уйдёт».
 */

const STATUS: Record<BugReportStatus, { label: string; tone: StatusTone }> = {
  New:       { label: 'новое',      tone: 'warning' },
  Forwarded: { label: 'передано',   tone: 'brand' },
  Fixed:     { label: 'исправлено', tone: 'success' },
  Rejected:  { label: 'отклонено',  tone: 'quiet' },
};

function ReportStatus({ status }: { status: BugReportStatus }) {
  const meta = STATUS[status];
  return <StatusBadge tone={meta.tone}>{meta.label}</StatusBadge>;
}

export function BugReportsPage() {
  const { data, isLoading } = useBugReports();
  const reports = data?.items;
  const [picked, setPicked] = useState<string | null>(null);
  const ld = useListDetailView();
  const [query, setQuery] = useState('');

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    return (reports ?? []).filter(r =>
      !q || r.summary.toLowerCase().includes(q) || r.author.toLowerCase().includes(q));
  }, [reports, query]);

  // Выбор ВЫЧИСЛЯЕМ, а не досылаем эффектом: пока никого не выбрали, открыто первое — а список
  // отсортирован «новые сверху», то есть первым оказывается то, что и ждёт разбора.
  const selectedId = picked ?? filtered[0]?.id ?? null;

  const open = filtered.filter(r => r.status === 'New' || r.status === 'Forwarded');
  const closed = filtered.filter(r => r.status === 'Fixed' || r.status === 'Rejected');

  const row = (r: BugReportListItem) => (
    <button key={r.id} type="button" onClick={() => { ld.showDetail(); setPicked(r.id); }}
      aria-current={r.id === selectedId ? 'true' : undefined}
      className={`state-layer w-full text-left px-3 py-2 rounded-lg ${
        r.id === selectedId ? 'bg-primary-container' : ''}`}>
      <div className="flex items-start gap-2">
        <span className={`flex-1 min-w-0 text-body-medium truncate ${r.id === selectedId ? 'text-primary font-medium' : 'text-on-surface'}`}>
          {r.summary}
        </span>
        <ReportStatus status={r.status} />
      </div>
      <div className="flex items-center gap-1.5 mt-0.5 text-label-small text-on-surface-variant">
        <span className="truncate">{r.author}</span>
        <span>·</span>
        <span className="shrink-0">{formatDayTime(r.createdAt)}</span>
        {r.hasScreenshot && <Icon icon={ImageIcon} size={18} className="shrink-0" aria-label="со снимком экрана" />}
        {r.githubIssueNumber && <span className="shrink-0">· #{r.githubIssueNumber}</span>}
      </div>
    </button>
  );

  return (
    <ListDetailShell view={ld}
      title="Сообщения об ошибках"
      subtitle={data && data.total > data.items.length
        ? `Показаны последние ${data.items.length} из ${data.total} — более старые в списке не видны`
        : 'Что пользователи сообщают из приложения. Наружу уходит только то, что вы отредактируете'}
      icon={<Icon icon={BugReport} size={20} className="text-on-surface-variant" />}
      overlay={isLoading
        ? <div className="flex-1 flex items-center justify-center text-on-surface-variant text-body-medium">Загрузка…</div>
        : (reports?.length ?? 0) === 0
          ? <div className="flex-1 flex items-center justify-center text-on-surface-variant text-body-medium">
              Сообщений нет. Пользователи отправляют их пунктом «Сообщить о проблеме» в меню профиля.
            </div>
          : undefined}
      nav={
        <div className="flex flex-col min-h-0">
          <NavSearchInput value={query} onChange={setQuery} placeholder="Поиск по тексту и автору…" />
          <div className="flex-1 min-h-0 overflow-y-auto px-2 pb-2 space-y-0.5">
            {open.length > 0 && <NavSection label="В разборе" />}
            {open.map(row)}
            {closed.length > 0 && <NavSection label="Закрытые" />}
            {closed.map(row)}
            {filtered.length === 0 && <p className="text-body-medium text-on-surface-variant px-3 py-2">Ничего не найдено</p>}
          </div>
        </div>
      }
      detail={selectedId
        ? <ReportDetail key={selectedId} id={selectedId} githubConfigured={data?.githubConfigured ?? false} />
        : <div className="flex-1 flex items-center justify-center text-on-surface-variant text-body-medium">Выберите сообщение</div>}
    />
  );
}

function ReportDetail({ id, githubConfigured }: { id: string; githubConfigured: boolean }) {
  const { data: report, isLoading } = useBugReport(id);
  if (isLoading || !report) {
    return <div className="flex-1 flex items-center justify-center text-on-surface-variant text-body-medium">Загрузка…</div>;
  }
  // Ключ по времени правки: пришёл новый текст с сервера (сохранили, сменили статус) — форма
  // пересобирается на нём. Эффект-синхронизация тут делала бы то же самое, но с лишним рендером.
  return <ReportBody key={report.updatedAt} report={report} githubConfigured={githubConfigured} />;
}

function ReportBody({ report, githubConfigured }: {
  report: BugReportDetail; githubConfigured: boolean;
}) {
  const toast = useToast();
  const openAttachment = useOpenAttachment();
  const saveDraft = useSaveBugReportDraft();
  const markFixed = useMarkBugReportFixed();
  const reject = useRejectBugReport();
  const reopen = useReopenBugReport();
  const forward = useForwardBugReport();

  const [draft, setDraft] = useState(report.issueDraft);
  const [fixOpen, setFixOpen] = useState(false);
  const [rejectOpen, setRejectOpen] = useState(false);
  const [version, setVersion] = useState('');
  const [fixError, setFixError] = useState<string | null>(null);
  const [techOpen, setTechOpen] = useState(false);
  const [forwardOpen, setForwardOpen] = useState(false);
  const [issueTitle, setIssueTitle] = useState('');
  const [forwardError, setForwardError] = useState<string | null>(null);

  const dirty = draft !== report.issueDraft;
  const closed = report.status === 'Fixed' || report.status === 'Rejected';

  async function copyDraft() {
    try {
      await navigator.clipboard.writeText(draft);
      toast.success('Текст скопирован.');
    } catch {
      toast.error('Буфер обмена недоступен — выделите текст и скопируйте вручную.');
    }
  }

  return (
    <div className="flex-1 min-h-0 flex flex-col">
      <div className="shrink-0 px-6 py-4 border-b border-outline-variant bg-surface flex items-start gap-3">
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2">
            <h2 className="text-body-large font-medium text-on-surface truncate">{report.author}</h2>
            <ReportStatus status={report.status} />
          </div>
          <p className="text-body-small text-on-surface-variant mt-0.5">
            {report.authorEmail && <>{report.authorEmail} · </>}{formatDayTime(report.createdAt)}
            {report.fixedInVersion && <> · исправлено в {report.fixedInVersion}</>}
          </p>
        </div>
        <div className="flex items-center gap-1.5 shrink-0">
          {closed ? (
            <Button variant="outlined" size="sm" onClick={() => reopen.mutate(report.id)}>
              <Icon icon={Undo} size={18} /> Вернуть в разбор
            </Button>
          ) : (
            <>
              {/* Кнопка есть и без токена — она ОБЪЯСНЯЕТ, чего не хватает. Спрятанная оставила бы
                  администратора гадать, куда делась передача. Уже переданное не показываем: второй
                  issue о том же убирают руками. */}
              {report.githubIssueNumber === null && (
                <Button variant="outlined" size="sm"
                  onClick={() => {
                    setForwardError(null);
                    setIssueTitle(prev => prev || defaultIssueTitle(report.message));
                    setForwardOpen(true);
                  }}>
                  <Icon icon={Send} size={18} /> Отправить в GitHub
                </Button>
              )}
              <Button variant="outlined" size="sm" onClick={() => setRejectOpen(true)}>
                <Icon icon={Close} size={18} /> Отклонить
              </Button>
              <Button variant="filled" size="sm"
                onClick={() => { setVersion(''); setFixError(null); setFixOpen(true); }}>
                <Icon icon={Check} size={18} /> Исправлено…
              </Button>
            </>
          )}
        </div>
      </div>

      <div className="flex-1 min-h-0 overflow-y-auto px-6 py-4 space-y-5">
        {/* ── Слова автора ─────────────────────────────────────────────── */}
        <section className="space-y-1.5">
          <h3 className="text-title-small text-on-surface-variant">Что произошло</h3>
          <p className="text-body-medium text-on-surface whitespace-pre-wrap break-words">{report.message}</p>
        </section>

        {report.screenshotBlobPath && (
          <section className="space-y-1.5">
            <h3 className="text-title-small text-on-surface-variant">Снимок экрана</h3>
            <Button variant="outlined" size="sm"
              onClick={() => { void openAttachment(report.screenshotBlobPath!); }}>
              <Icon icon={OpenInNew} size={18} /> Открыть снимок
            </Button>
            <p className="text-body-small text-on-surface-variant">В GitHub снимок не передаётся — он остаётся здесь.</p>
          </section>
        )}

        {/* ── Техблок ──────────────────────────────────────────────────── */}
        <section>
          <button type="button" onClick={() => setTechOpen(o => !o)}
            className="flex items-center gap-1.5 text-title-small text-on-surface-variant">
            {techOpen ? <Icon icon={KeyboardArrowDown} size={18} /> : <Icon icon={ChevronRight} size={18} />} Технический контекст
          </button>
          {techOpen && <TechBlock tech={report.tech} />}
        </section>

        {/* ── Текст issue ──────────────────────────────────────────────── */}
        <section className="space-y-2">
          <div className="flex items-center justify-between gap-2">
            <h3 className="text-title-small text-on-surface-variant">
              Текст issue {report.draftEdited ? '· отредактирован' : '· заготовка'}
            </h3>
            <div className="flex items-center gap-1.5">
              {dirty && (
                <Button variant="text" size="sm" onClick={() => setDraft(report.issueDraft)}>Отмена</Button>
              )}
              <Button variant="outlined" size="sm" disabled={!dirty} loading={saveDraft.isPending}
                onClick={() => saveDraft.mutate({ id: report.id, text: draft })}>
                Сохранить
              </Button>
              <Button variant="text" size="sm" onClick={() => { void copyDraft(); }}>
                <Icon icon={ContentCopy} size={18} /> Скопировать
              </Button>
            </div>
          </div>
          <textarea
            value={draft}
            onChange={e => setDraft(e.target.value)}
            rows={16}
            spellCheck={false}
            className="w-full rounded-lg border border-outline bg-surface px-3 py-2 font-mono text-body-small leading-relaxed text-on-surface focus:outline-none focus-visible:ring-1 focus-visible:ring-primary"
          />
          <p className="text-body-small text-on-surface-variant">
            Markdown. Уберите внутреннее — названия строек, организаций и объектов: репозиторий
            публичный. Заголовок issue вы напишете сами при создании.
            {report.githubIssueUrl && (
              <> {' '}<a href={report.githubIssueUrl} target="_blank" rel="noreferrer"
                className="text-primary hover:underline">Открыть issue #{report.githubIssueNumber}</a></>
            )}
          </p>
          {/* «Скопировать» осталось рядом с отправкой намеренно: issue заводят не только из этой
              системы, и перенести текст руками иногда быстрее, чем разбираться с токеном. */}
        </section>
      </div>

      {/*
        Своя модалка, а не ConfirmDialog: здесь ВВОДЯТ заголовок, а тот про подтверждение решения
        (тот же случай, что с «Исправлено в версии»). Без токена показываем не поле, а объяснение —
        отправлять всё равно нечем, и притворяться готовой формой было бы враньём.
      */}
      <Modal
        open={forwardOpen}
        onOpenChange={o => { if (!o) setForwardOpen(false); }}
        title={githubConfigured ? 'Отправить в GitHub' : 'Токен GitHub не задан'}
        footer={
          <div className="flex items-center justify-end gap-2">
            <Button variant="text" onClick={() => setForwardOpen(false)}>
              {githubConfigured ? 'Отмена' : 'Понятно'}
            </Button>
            {githubConfigured && (
              <Button variant="filled" disabled={!issueTitle.trim()} loading={forward.isPending}
                onClick={async () => {
                  setForwardError(null);
                  try {
                    // draft — то, что СЕЙЧАС в поле «Текст issue», сохранён он или нет.
                    await forward.mutateAsync({ id: report.id, title: issueTitle, body: draft });
                    setForwardOpen(false);
                    toast.success('Передано разработчикам.');
                  } catch (e) {
                    setForwardError(apiError(e, 'Не удалось создать issue.'));
                  }
                }}>
                Создать issue
              </Button>
            )}
          </div>
        }
      >
        {githubConfigured ? (
          <div className="space-y-3">
            <p className="text-body-medium text-error">
              После отправки текст станет публичным: репозиторий открыт для всех. Проверьте, что в
              нём не осталось названий строек, организаций и объектов.
            </p>
            <TextField label="Заголовок issue" value={issueTitle} autoFocus
              onChange={e => setIssueTitle(e.target.value)}
              hint="По нему issue будут искать в трекере" />
            <p className="text-body-small text-on-surface-variant">
              Телом уйдёт текст из поля «Текст issue» — ровно в том виде, в каком он сейчас на
              экране{dirty ? ', вместе с несохранёнными правками (они и сохранятся)' : ''}. Снимок
              экрана не передаётся.
            </p>
            {forwardError && (
              <p className="flex items-start gap-1.5 text-body-medium text-error">
                <Icon icon={Warning} size={18} className="shrink-0 mt-0.5" /> {forwardError}
              </p>
            )}
          </div>
        ) : (
          <div className="space-y-2 text-body-medium text-on-surface-variant">
            <p>
              Чтобы заводить issue из системы, нужен токен GitHub с правом <b>issues: write</b> на
              репозиторий продукта.
            </p>
            <p className="text-on-surface-variant">
              Укажите его в <Link to="/settings" className="text-primary hover:underline">настройках
              интеграций</Link>, раздел «Передача в GitHub». До этого текст можно перенести кнопкой
              «Скопировать».
            </p>
          </div>
        )}
      </Modal>

      <ConfirmDialog
        open={rejectOpen} onOpenChange={setRejectOpen}
        title="Отклонить сообщение?"
        description="Автор получит уведомление, что разработки по нему не будет. Решение обратимо — сообщение можно вернуть в разбор."
        confirmLabel="Отклонить" confirmDanger={false}
        // Без своего заголовка отказ пришёл бы под чужим: у ConfirmDialog умолчание —
        // «Удаление невозможно», а здесь ничего не удаляют.
        errorTitle="Не удалось отклонить"
        onConfirm={() => reject.mutateAsync(report.id)}
      />

      {/*
        Своя модалка, а не ConfirmDialog: здесь ВВОДЯТ значение, а тот про подтверждение решения.
        Подставленный в него ввод вёл себя дурно — кнопка была активна при пустом поле, а отказ
        сервера («укажите версию») превращал диалог в панель «Удаление невозможно» с единственной
        кнопкой «Понятно», стирая набранное.
      */}
      <Modal
        open={fixOpen}
        onOpenChange={o => { if (!o) setFixOpen(false); }}
        title="Исправлено в версии"
        footer={
          <div className="flex items-center justify-end gap-2">
            <Button variant="text" onClick={() => setFixOpen(false)}>Отмена</Button>
            <Button variant="filled" disabled={!version.trim()} loading={markFixed.isPending}
              onClick={async () => {
                setFixError(null);
                try {
                  await markFixed.mutateAsync({ id: report.id, version });
                  setFixOpen(false);
                } catch (e) {
                  setFixError(apiError(e, 'Не удалось отметить исправленным.'));
                }
              }}>
              Отметить исправленным
            </Button>
          </div>
        }
      >
        <div className="space-y-3">
          <p className="text-body-medium text-on-surface-variant">
            Автор получит уведомление с этим номером — по нему он поймёт, ждать ли обновления.
          </p>
          <TextField label="Версия" value={version} autoFocus
            onChange={e => setVersion(e.target.value)} hint="Например, 0.145.0" />
          {fixError && (
            <p className="flex items-start gap-1.5 text-body-medium text-error">
              <Icon icon={Warning} size={18} className="shrink-0 mt-0.5" /> {fixError}
            </p>
          )}
        </div>
      </Modal>
    </div>
  );
}

const API_ERROR_COLUMNS: DataGridColumn<NonNullable<BugReportTech['apiErrors']>[number]>[] = [
  { key: 'at', label: 'время' },
  { key: 'url', label: 'запрос', wrap: true, cell: e => <span className="break-all">{e.method} {e.url}</span> },
  { key: 'status', label: 'код', cell: e => `${e.status || '—'}${(e.count ?? 1) > 1 ? ` ×${e.count}` : ''}` },
  { key: 'traceId', label: 'идентификатор запроса', wrap: true, cell: e => <span className="break-all">{e.traceId ?? '—'}</span> },
];

function TechBlock({ tech }: { tech: BugReportTech | null }) {
  if (!tech) return <p className="mt-2 text-body-medium text-on-surface-variant">Клиент не прислал технический контекст.</p>;
  return (
    <div className="mt-2 space-y-2 text-body-small text-on-surface-variant">
      {tech.dropped && <p className="text-warning">{tech.dropped}</p>}
      <ul className="space-y-0.5">
        {tech.version && <li>Версия при загрузке страницы: {tech.version}{tech.commit ? ` · ${tech.commit}` : ''}</li>}
        {tech.server?.version && (
          <li>Версия сервера: {tech.server.version}{tech.server.commit ? ` · ${tech.server.commit}` : ''}</li>
        )}
        {tech.route && <li>Экран: <span className="font-mono">{tech.route}</span></li>}
        {tech.viewport && <li>Окно: {tech.viewport}</li>}
        {tech.origin && <li>Открыто из: {originLabel(tech.origin)}</li>}
        {tech.userAgent && <li className="break-all">Браузер: {tech.userAgent}</li>}
      </ul>

      {(tech.apiErrors?.length ?? 0) > 0 && (
        <DataGrid density="compact" className="font-mono" columns={API_ERROR_COLUMNS} rows={tech.apiErrors!} />
      )}

      {tech.stack && (
        <pre className="max-h-60 overflow-auto rounded-md bg-surface-container p-3 text-label-small leading-relaxed whitespace-pre-wrap break-words">
          {tech.stack}
        </pre>
      )}
    </div>
  );
}

/**
 * Заготовка заголовка issue — первая строка сообщения.
 *
 * Именно заготовка: заголовок пишет администратор, он один видел, что из текста убрано. Но пустое
 * поле в диалоге отправки означало бы, что первую строку перепечатывают руками каждый раз.
 */
function defaultIssueTitle(message: string): string {
  const line = message.split('\n').map(l => l.trim()).find(Boolean) ?? '';
  return line.length <= 120 ? line : line.slice(0, 120).trimEnd() + '…';
}

function originLabel(origin: string): string {
  if (origin === 'account') return 'меню профиля';
  // Сообщения, отправленные до переезда пункта в меню профиля (#1430).
  if (origin === 'rail') return 'боковая панель';
  if (origin === 'boundary') return 'экран сбоя интерфейса';
  if (origin === 'toast') return 'уведомление об ошибке';
  return origin;
}
