import { useMemo, useState } from 'react';
import {
  AlertTriangle, Bug, Check, ChevronDown, ChevronRight, Copy, ExternalLink,
  Image as ImageIcon, Undo2, X,
} from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { Modal } from '@/shared/ui/Modal';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { apiError } from '@/shared/utils/apiError';
import { ListDetailShell, NavSearchInput, NavSection } from '@/shared/ui/ListDetailShell';
import { useToast } from '@/shared/ui/Toast';
import { openAttachmentInNewTab } from '@/shared/api/attachments';
import {
  useBugReports, useBugReport, useSaveBugReportDraft, useMarkBugReportFixed,
  useRejectBugReport, useReopenBugReport,
  type BugReportDetail, type BugReportListItem, type BugReportStatus, type BugReportTech,
} from '@/shared/api/bugReports';

/**
 * Разбор сообщений об ошибках (issue #834).
 *
 * Экран администратора и есть тот фильтр, ради которого сообщения не уходят в GitHub напрямую:
 * репозиторий публичный, а пользователь описывает беду названиями строек и организаций. Здесь текст
 * правят ДО публикации — потому поле «Текст issue» редактируемое, а не показ «как уйдёт».
 */

const STATUS: Record<BugReportStatus, { label: string; className: string }> = {
  New:       { label: 'новое',     className: 'bg-warning-subtle text-warning' },
  Forwarded: { label: 'передано',  className: 'bg-brand-subtle text-brand' },
  Fixed:     { label: 'исправлено', className: 'bg-success-subtle text-success' },
  Rejected:  { label: 'отклонено', className: 'text-fg3 border border-stroke' },
};

function StatusBadge({ status }: { status: BugReportStatus }) {
  const meta = STATUS[status];
  return (
    <span className={`shrink-0 inline-flex items-center h-5 px-2 rounded-full text-[11px] font-medium ${meta.className}`}>
      {meta.label}
    </span>
  );
}

function when(iso: string): string {
  return new Date(iso).toLocaleString('ru-RU', {
    day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit',
  });
}

export function BugReportsPage() {
  const { data, isLoading } = useBugReports();
  const reports = data?.items;
  const [picked, setPicked] = useState<string | null>(null);
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
    <button key={r.id} type="button" onClick={() => setPicked(r.id)}
      aria-current={r.id === selectedId ? 'true' : undefined}
      className={`w-full text-left px-3 py-2 rounded-xl transition-colors ${
        r.id === selectedId ? 'bg-brand-subtle' : 'hover:bg-muted'}`}>
      <div className="flex items-start gap-2">
        <span className={`flex-1 min-w-0 text-sm truncate ${r.id === selectedId ? 'text-brand-hover font-medium' : 'text-fg1'}`}>
          {r.summary}
        </span>
        <StatusBadge status={r.status} />
      </div>
      <div className="flex items-center gap-1.5 mt-0.5 text-[11px] text-fg4">
        <span className="truncate">{r.author}</span>
        <span>·</span>
        <span className="shrink-0">{when(r.createdAt)}</span>
        {r.hasScreenshot && <ImageIcon size={11} className="shrink-0" aria-label="со снимком экрана" />}
        {r.githubIssueNumber && <span className="shrink-0">· #{r.githubIssueNumber}</span>}
      </div>
    </button>
  );

  return (
    <ListDetailShell
      title="Сообщения об ошибках"
      subtitle={data && data.total > data.items.length
        ? `Показаны последние ${data.items.length} из ${data.total} — более старые в списке не видны`
        : 'Что пользователи сообщают из приложения. Наружу уходит только то, что вы отредактируете'}
      titleIcon={<Bug size={20} className="text-fg3" />}
      overlay={isLoading
        ? <div className="flex-1 flex items-center justify-center text-fg4 text-sm">Загрузка…</div>
        : (reports?.length ?? 0) === 0
          ? <div className="flex-1 flex items-center justify-center text-fg4 text-sm">
              Сообщений нет. Пользователи отправляют их пунктом «Сообщить об ошибке» в боковой панели.
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
            {filtered.length === 0 && <p className="text-sm text-fg4 px-3 py-2">Ничего не найдено</p>}
          </div>
        </div>
      }
      detail={selectedId
        ? <ReportDetail key={selectedId} id={selectedId} />
        : <div className="flex-1 flex items-center justify-center text-fg4 text-sm">Выберите сообщение</div>}
    />
  );
}

function ReportDetail({ id }: { id: string }) {
  const { data: report, isLoading } = useBugReport(id);
  if (isLoading || !report) {
    return <div className="flex-1 flex items-center justify-center text-fg4 text-sm">Загрузка…</div>;
  }
  // Ключ по времени правки: пришёл новый текст с сервера (сохранили, сменили статус) — форма
  // пересобирается на нём. Эффект-синхронизация тут делала бы то же самое, но с лишним рендером.
  return <ReportBody key={report.updatedAt} report={report} />;
}

function ReportBody({ report }: { report: BugReportDetail }) {
  const toast = useToast();
  const saveDraft = useSaveBugReportDraft();
  const markFixed = useMarkBugReportFixed();
  const reject = useRejectBugReport();
  const reopen = useReopenBugReport();

  const [draft, setDraft] = useState(report.issueDraft);
  const [fixOpen, setFixOpen] = useState(false);
  const [rejectOpen, setRejectOpen] = useState(false);
  const [version, setVersion] = useState('');
  const [fixError, setFixError] = useState<string | null>(null);
  const [techOpen, setTechOpen] = useState(false);

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
      <div className="shrink-0 px-6 py-4 border-b border-stroke bg-surface flex items-start gap-3">
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2">
            <h2 className="text-base font-medium text-fg1 truncate">{report.author}</h2>
            <StatusBadge status={report.status} />
          </div>
          <p className="text-xs text-fg3 mt-0.5">
            {report.authorEmail && <>{report.authorEmail} · </>}{when(report.createdAt)}
            {report.fixedInVersion && <> · исправлено в {report.fixedInVersion}</>}
          </p>
        </div>
        <div className="flex items-center gap-1.5 shrink-0">
          {closed ? (
            <Button variant="outlined" size="sm" onClick={() => reopen.mutate(report.id)}>
              <Undo2 size={14} /> Вернуть в разбор
            </Button>
          ) : (
            <>
              <Button variant="outlined" size="sm" onClick={() => setRejectOpen(true)}>
                <X size={14} /> Отклонить
              </Button>
              <Button variant="filled" size="sm"
                onClick={() => { setVersion(''); setFixError(null); setFixOpen(true); }}>
                <Check size={14} /> Исправлено…
              </Button>
            </>
          )}
        </div>
      </div>

      <div className="flex-1 min-h-0 overflow-y-auto px-6 py-4 space-y-5">
        {/* ── Слова автора ─────────────────────────────────────────────── */}
        <section className="space-y-1.5">
          <h3 className="text-xs font-semibold uppercase tracking-wide text-fg4">Что произошло</h3>
          <p className="text-sm text-fg1 whitespace-pre-wrap break-words">{report.message}</p>
        </section>

        {report.screenshotBlobPath && (
          <section className="space-y-1.5">
            <h3 className="text-xs font-semibold uppercase tracking-wide text-fg4">Снимок экрана</h3>
            <Button variant="outlined" size="sm"
              onClick={() => { void openAttachmentInNewTab(report.screenshotBlobPath!); }}>
              <ExternalLink size={14} /> Открыть снимок
            </Button>
            <p className="text-xs text-fg4">В GitHub снимок не передаётся — он остаётся здесь.</p>
          </section>
        )}

        {/* ── Техблок ──────────────────────────────────────────────────── */}
        <section>
          <button type="button" onClick={() => setTechOpen(o => !o)}
            className="flex items-center gap-1.5 text-xs font-semibold uppercase tracking-wide text-fg4 hover:text-fg2 transition-colors">
            {techOpen ? <ChevronDown size={13} /> : <ChevronRight size={13} />} Технический контекст
          </button>
          {techOpen && <TechBlock tech={report.tech} />}
        </section>

        {/* ── Текст issue ──────────────────────────────────────────────── */}
        <section className="space-y-2">
          <div className="flex items-center justify-between gap-2">
            <h3 className="text-xs font-semibold uppercase tracking-wide text-fg4">
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
                <Copy size={14} /> Скопировать
              </Button>
            </div>
          </div>
          <textarea
            value={draft}
            onChange={e => setDraft(e.target.value)}
            rows={16}
            spellCheck={false}
            className="w-full rounded-xl border border-stroke-strong bg-surface px-3 py-2 font-mono text-[12.5px] leading-relaxed text-fg1 focus:outline-none focus-visible:ring-1 focus-visible:ring-brand"
          />
          <p className="text-xs text-fg4">
            Markdown. Уберите внутреннее — названия строек, организаций и объектов: репозиторий
            публичный. Заголовок issue вы напишете сами при создании.
            {report.githubIssueUrl && (
              <> {' '}<a href={report.githubIssueUrl} target="_blank" rel="noreferrer"
                className="text-brand hover:underline">Открыть issue #{report.githubIssueNumber}</a></>
            )}
          </p>
          {/* Отправку в GitHub добавит вторая часть issue #834 — вместе с токеном в настройках
              интеграций. До неё текст переносят копированием: это ровно та же работа руками,
              которой сегодня нет вовсе. */}
        </section>
      </div>

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
          <p className="text-sm text-fg2">
            Автор получит уведомление с этим номером — по нему он поймёт, ждать ли обновления.
          </p>
          <TextField label="Версия" value={version} autoFocus
            onChange={e => setVersion(e.target.value)} hint="Например, 0.145.0" />
          {fixError && (
            <p className="flex items-start gap-1.5 text-sm text-danger">
              <AlertTriangle size={14} className="shrink-0 mt-0.5" /> {fixError}
            </p>
          )}
        </div>
      </Modal>
    </div>
  );
}

function TechBlock({ tech }: { tech: BugReportTech | null }) {
  if (!tech) return <p className="mt-2 text-sm text-fg4">Клиент не прислал технический контекст.</p>;
  return (
    <div className="mt-2 space-y-2 text-xs text-fg3">
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
        <div className="overflow-x-auto">
          <table className="w-full text-[11px] font-mono">
            <thead className="text-fg4">
              <tr><th className="text-left pr-3 font-medium">время</th>
                <th className="text-left pr-3 font-medium">запрос</th>
                <th className="text-left pr-3 font-medium">код</th>
                <th className="text-left font-medium">идентификатор запроса</th></tr>
            </thead>
            <tbody>
              {tech.apiErrors!.map((e, i) => (
                <tr key={i}>
                  <td className="pr-3 whitespace-nowrap">{e.at}</td>
                  <td className="pr-3 break-all">{e.method} {e.url}</td>
                  <td className="pr-3">
                    {e.status || '—'}{(e.count ?? 1) > 1 ? ` ×${e.count}` : ''}
                  </td>
                  <td className="break-all">{e.traceId ?? '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {tech.stack && (
        <pre className="max-h-60 overflow-auto rounded-lg bg-black/5 dark:bg-white/5 p-3 text-[11px] leading-relaxed whitespace-pre-wrap break-words">
          {tech.stack}
        </pre>
      )}
    </div>
  );
}

function originLabel(origin: string): string {
  if (origin === 'rail') return 'боковая панель';
  if (origin === 'boundary') return 'экран сбоя интерфейса';
  if (origin === 'toast') return 'уведомление об ошибке';
  return origin;
}
