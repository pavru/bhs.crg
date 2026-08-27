import { useState } from 'react';
import { Loader2, FileText, Download, Eye, Bug, ShieldCheck, AlertTriangle, AlertCircle, CheckCircle2, Mail, Stethoscope } from 'lucide-react';
import { useAuth } from '@/shared/hooks/useAuth';
import { useEmailDocument } from '@/shared/api/documentSets';
import { EmailSendDialog } from '../EmailSendDialog';
import { useGenerateDocument, useSetDocumentTemplates, downloadGeneratedFile, previewGeneratedFile, downloadDebugBundle, useResolutionDiagnostics, type ResolutionDiagnostic } from '@/shared/api/documentSets';
import { useListTemplates } from '@/shared/api/templates';
import type { DocumentInstance, Template } from '@/shared/api/types';
import { STATUS_LABELS, STATUS_COLORS } from '../fields';
import { InstanceAuditModal } from './InstanceAuditModal';
import { DocumentTemplateParams } from './DocumentTemplateParams';
import { Button } from '@/shared/ui/Button';
import { containsRef } from './brokenRefs';

// ── Вкладка «Генерация» ─────────────────────────────────────────────────────────
// Выделено из editor/index.tsx (#490): файл на 1220 строк был оболочкой вкладок и двумя
// самими вкладками. Перенос без изменения поведения — вкладки ничего не разделяли между
// собой, кроме мелких помощников, которые уехали в отдельный модуль.

/** Парсит JSON-строку массива id (templateIds) в массив; безопасно к битому/пустому значению. */
function parseIdArray(json: string | null): string[] {
  if (!json) return [];
  try { const a = JSON.parse(json); return Array.isArray(a) ? a as string[] : []; } catch { return []; }
}

function DiagnosticsPanel({ diagnostics, objectName }: { diagnostics: ResolutionDiagnostic[]; objectName: string }) {
  if (diagnostics.length === 0) {
    return (
      <div className="flex items-center gap-2 p-3 rounded-md text-sm bg-success-subtle text-success">
        <CheckCircle2 size={15} className="shrink-0" /> Проблем разрешения ссылок не найдено
      </div>
    );
  }
  const errors = diagnostics.filter(d => d.severity === 'error');
  const warnings = diagnostics.filter(d => d.severity === 'warning');
  return (
    <div className="rounded-md border border-stroke overflow-hidden">
      <div className="px-3 py-2 text-xs font-medium bg-base text-fg2">
        Диагностика ссылок — объект «{objectName}»: {errors.length} ошиб., {warnings.length} предупр.
      </div>
      <div className="divide-y divide-muted max-h-72 overflow-y-auto">
        {[...errors, ...warnings].map((d, i) => (
          <div key={i} className="flex items-start gap-2 px-3 py-2 text-xs">
            {d.severity === 'error'
              ? <AlertCircle size={14} className="text-danger shrink-0 mt-0.5" />
              : <AlertTriangle size={14} className="text-warning shrink-0 mt-0.5" />}
            <div className="min-w-0">
              <div className="text-fg3">Реквизит: <code className="text-fg2">{d.path}</code></div>
              <p className={d.severity === 'error' ? 'text-danger' : 'text-fg2'}>{d.message}</p>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

function extractDiagnostics(err: unknown): ResolutionDiagnostic[] | null {
  const data = (err as { response?: { data?: { diagnostics?: ResolutionDiagnostic[] } } })?.response?.data;
  return Array.isArray(data?.diagnostics) ? data!.diagnostics! : null;
}

export function GenerationTab({ instance, setId, schemaFieldKeys }: { instance: DocumentInstance; setId: string; schemaFieldKeys: string[] }) {
  const [auditOpen, setAuditOpen] = useState(false);
  const { user } = useAuth();
  const isAdmin = user?.role === 'Admin';
  const [emailOpen, setEmailOpen] = useState(false);
  const emailDoc = useEmailDocument();
  const [error, setError] = useState('');
  const [debugBusy, setDebugBusy] = useState(false);
  // Диагностика ссылок из общего кэша (issue #334): те же данные, что и индикаторы битых ссылок на
  // полях реквизитов — один запрос, без расхождений. Локальный override — результат генерации-гейта.
  const { data: sharedDiagnostics, refetch: refetchDiagnostics, isFetching: validating } =
    useResolutionDiagnostics(instance.id, containsRef(instance.requisites));
  const [diagnostics, setDiagnostics] = useState<ResolutionDiagnostic[] | null>(null);
  const shownDiagnostics = diagnostics ?? sharedDiagnostics ?? null;
  const mutation = useGenerateDocument();
  const setTemplatesMutation = useSetDocumentTemplates();
  const { data: templates = [], isLoading: templatesLoading } = useListTemplates(instance.documentTypeId);
  const activeTemplates = templates.filter((t: Template) => t.isActive);
  const noTemplates = !templatesLoading && activeTemplates.length === 0;
  /**
   * Выбор шаблонов — локальный ОВЕРРАЙД поверх серверного значения (issue #858), а не копия,
   * которую эффект переливал на каждое изменение пропа.
   *
   * <p>Храним вместе с ним, от какого серверного значения он отсчитан. Тогда «правил ли человек» и
   * «не устарела ли правка» — один и тот же вопрос: пришло другое серверное значение, оверрайд ему
   * больше не отвечает, и показывается свежее. Эффектом это стоило лишнего коммита, в котором
   * форма показывала выбор от прошлого документа.</p>
   */
  const [localSelection, setLocalSelection] =
    useState<{ from: string | null | undefined; ids: string[] } | null>(null);
  const selectedTemplateIds = localSelection && localSelection.from === instance.templateIds
    ? localSelection.ids
    : parseIdArray(instance.templateIds);
  // Эффективный шаблон для параметров/дефолт-скачивания: первый выбранный → по умолчанию → первый активный.
  const effectiveTemplate = activeTemplates.find((t: Template) => t.id === selectedTemplateIds[0])
    ?? activeTemplates.find((t: Template) => t.id === instance.templateId)
    ?? activeTemplates.find((t: Template) => t.isDefault)
    ?? activeTemplates[0];
  // Фокус — какой шаблон сейчас «раскрыт» в блоке параметров (ортогонально членству в генерации).
  // По умолчанию — эффективный; держим фокус при переключении галок, если он ещё валиден.
  // Выбранный человеком фокус держится, пока такой шаблон вообще есть среди активных; иначе
  // показываем эффективный. Это вычисление, а не состояние: список шаблонов приходит с сервера и
  // может смениться, и эффект-сторож просто повторял бы здесь ту же проверку — коммитом позже.
  const [chosenFocus, setChosenFocus] = useState<string | null>(null);
  const focusedTemplateId = chosenFocus && activeTemplates.some((t: Template) => t.id === chosenFocus)
    ? chosenFocus
    : (effectiveTemplate?.id ?? null);
  const focusedTemplate = activeTemplates.find((t: Template) => t.id === focusedTemplateId) ?? effectiveTemplate;

  async function handleGenerate() {
    setError('');
    setDiagnostics(null);
    try { await mutation.mutateAsync({ instanceId: instance.id, setId }); }
    catch (err: unknown) {
      const diag = extractDiagnostics(err);
      if (diag) { setDiagnostics(diag); setError('Генерация прервана: ошибки разрешения ссылок'); }
      else setError(err instanceof Error ? err.message : 'Ошибка');
    }
  }

  async function handleValidate() {
    setError('');
    setDiagnostics(null); // снять локальный override → показать свежий общий результат
    try { await refetchDiagnostics(); }
    catch (err: unknown) { setError(err instanceof Error ? err.message : 'Ошибка'); }
  }

  async function handleDebugBundle() {
    setError('');
    setDebugBusy(true);
    try { await downloadDebugBundle(instance.id); }
    catch (err: unknown) { setError(err instanceof Error ? err.message : 'Ошибка'); }
    finally { setDebugBusy(false); }
  }

  function toggleTemplate(id: string, on: boolean) {
    // Базой берём показанный выбор — он и есть «последний»: оверрайд помнит, от какого серверного
    // значения отсчитан, поэтому запоздавший ответ сервера его больше не перебивает. Ровно ради
    // этого раньше стоял функциональный апдейтер — и мутация уезжала ИЗНУТРИ него, то есть
    // побочным действием в редьюсере: под StrictMode React зовёт апдейтер дважды, и каждый щелчок
    // слал два PUT (поймано ревью PR #862).
    const next = on
      ? [...new Set([...selectedTemplateIds, id])]
      : selectedTemplateIds.filter(x => x !== id);
    setLocalSelection({ from: instance.templateIds, ids: next });
    setTemplatesMutation.mutate({ setId, instanceId: instance.id, templateIds: next });
  }

  const pdfFiles = instance.generatedFiles.filter(f => f.format === 'Pdf');
  // Идёт генерация (файлы перезаписываются) — блокируем открытие/скачивание, чтобы не попасть на
  // заменяемую/устаревшую версию (ссылка на «старый» файл → пустая страница до обновления).
  const busy = mutation.isPending || instance.status === 'Generating';

  return (
    <div className="space-y-5">
      <div className={`p-3 rounded-md text-sm ${STATUS_COLORS[instance.status] ?? 'bg-base text-fg2'}`}>
        Статус: <strong>{STATUS_LABELS[instance.status] ?? instance.status}</strong>
        {instance.status === 'Generating' && <Loader2 size={14} className="inline-block ml-2 animate-spin" />}
      </div>

      <div className="space-y-1">
        <label className="block text-xs font-medium text-fg2">Шаблоны <span className="text-fg4 font-normal">(можно несколько — по PDF на каждый)</span></label>
        {noTemplates ? (
          <p className="text-xs text-warning">
            Для этого типа документа нет шаблонов. Создайте шаблон в разделе «Шаблоны».
          </p>
        ) : (
          <>
            {/* Клик по ВСЕЙ строке (обёртка-label) переключает участие в генерации (issue #316);
                фокус (показ параметров ниже) следует за кликом через onChange. Галочка отражает участие,
                подсветка строки — фокус. */}
            <div className="rounded-md border border-stroke-strong divide-y divide-stroke overflow-hidden">
              {activeTemplates.map((t: Template) => {
                const selected = selectedTemplateIds.includes(t.id);
                const focused = focusedTemplate?.id === t.id;
                return (
                  <label key={t.id}
                    className={`flex items-center gap-2 pr-2.5 text-sm border-l-2 transition-colors cursor-pointer ${focused ? 'bg-brand-subtle border-brand' : 'border-transparent hover:bg-base'}`}>
                    <input type="checkbox" checked={selected} disabled={setTemplatesMutation.isPending}
                      onChange={e => { toggleTemplate(t.id, e.target.checked); setChosenFocus(t.id); }}
                      aria-label={`Использовать шаблон «${t.name}» для генерации`}
                      className="ml-2.5 shrink-0" />
                    <span className="flex-1 min-w-0 py-1.5">
                      <span className={`truncate ${focused ? 'text-brand-hover font-medium' : 'text-fg1'}`}>
                        {t.isDefault ? '★ ' : ''}{t.name} <span className="text-fg4 font-normal">(v{t.version})</span>
                      </span>
                    </span>
                  </label>
                );
              })}
            </div>
            {selectedTemplateIds.length === 0 && (
              <p className="text-[11px] text-fg4">Ничего не выбрано — будет один PDF по шаблону по умолчанию.</p>
            )}
          </>
        )}
      </div>

      {focusedTemplate && (
        <DocumentTemplateParams setId={setId} instance={instance} template={focusedTemplate}
          participating={selectedTemplateIds.length === 0 || selectedTemplateIds.includes(focusedTemplate.id)} />
      )}

      <div className="flex gap-3">
        <Button variant="filled" onClick={() => handleGenerate()} disabled={noTemplates}
          loading={mutation.isPending} icon={<FileText size={14} />}>
          Сгенерировать PDF
        </Button>
        <Button variant="outlined" onClick={handleValidate} loading={validating}
          title="Проверить разрешение ссылок (каталог, наборы данных) без генерации"
          icon={<ShieldCheck size={14} />}>
          Проверить ссылки
        </Button>
        <Button variant="outlined" onClick={() => setAuditOpen(true)}
          title="Найти в документе поля, которых нет в текущей схеме типа, и исправить"
          icon={<Stethoscope size={14} />}>
          Аудит документа
        </Button>
        <InstanceAuditModal setId={setId} instanceId={instance.id} docName={instance.name || 'без названия'}
          schemaFieldKeys={schemaFieldKeys} open={auditOpen} onClose={() => setAuditOpen(false)} />
        <Button variant="outlined" onClick={handleDebugBundle} disabled={debugBusy || noTemplates}
          loading={debugBusy}
          title="Скачать ZIP с template.typ, data.json, typeblocks.typ и userlib.typ для отладки во внешнем инструменте (typst compile template.typ)"
          icon={<Bug size={14} />}>
          Отладочный пакет
        </Button>
      </div>
      {(mutation.isPending || instance.status === 'Generating') && (
        <p className="flex items-center gap-2 text-xs text-fg4">
          <Loader2 size={12} className="animate-spin shrink-0" />
          Генерация PDF может занять несколько секунд — идёт сбор данных и компиляция Typst.
        </p>
      )}
      {error && <p className="text-sm text-danger">{error}</p>}

      {shownDiagnostics && <DiagnosticsPanel diagnostics={shownDiagnostics} objectName={instance.name || 'без названия'} />}
      {pdfFiles.length > 0 && (
        <div className="space-y-2">
          <div className="flex items-center justify-between">
            <p className="text-xs font-medium text-fg2">Сгенерированные файлы</p>
            {isAdmin && (
              <button onClick={() => setEmailOpen(true)}
                className="flex items-center gap-1.5 text-xs px-2.5 py-1.5 border border-stroke rounded-md hover:bg-base transition-colors"
                title="Отправить сгенерированные PDF документа по почте (подписчикам и/или на внешние адреса)">
                <Mail size={13} className="text-brand" /> Отправить по почте
              </button>
            )}
          </div>
          <div className="space-y-1.5">
            {pdfFiles.map(f => {
              const tpl = templates.find((t: Template) => t.id === f.templateId);
              return (
                <div key={f.id} className="flex items-center gap-2">
                  <span className="text-xs text-fg2 flex-1 min-w-0 truncate" title={tpl?.name}>{tpl ? tpl.name : 'PDF'}</span>
                  {/* Во время генерации файл перезаписывается — блокируем открытие/скачивание, чтобы
                      не открыть неактуальную/заменяемую версию (ссылка ведёт на старое состояние). */}
                  <button onClick={() => previewGeneratedFile(instance.id, f.templateId)} disabled={busy}
                    className="flex items-center gap-1 px-2.5 py-1.5 text-xs border border-stroke rounded-md hover:bg-base disabled:opacity-40 disabled:pointer-events-none">
                    <Eye size={13} className="text-brand" /> Открыть
                  </button>
                  <button onClick={() => downloadGeneratedFile(instance.id, f.templateId)} disabled={busy}
                    className="flex items-center gap-1 px-2.5 py-1.5 text-xs border border-stroke rounded-md hover:bg-base disabled:opacity-40 disabled:pointer-events-none">
                    <Download size={13} className="text-brand" /> Скачать
                  </button>
                </div>
              );
            })}
          </div>
        </div>
      )}

      {isAdmin && (
        <EmailSendDialog open={emailOpen} onClose={() => setEmailOpen(false)}
          setId={setId} itemName={`Документ «${instance.name || 'документ'}»`}
          defaultSubjectHint={`Исполнительная документация — ${instance.name || 'документ'}`}
          defaultBodyHint={`Направляем документ «${instance.name || 'документ'}» исполнительной документации.`}
          ready={pdfFiles.length > 0} notReadyHint="У документа нет сгенерированных PDF — сначала сгенерируйте."
          onSend={(to, subject, body) => emailDoc.mutateAsync({ setId, instanceId: instance.id, to, subject, body })} />
      )}
    </div>
  );
}
