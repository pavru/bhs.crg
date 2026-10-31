import { useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { CircularProgress } from '@/shared/ui/CircularProgress';
import { BugReport, CheckCircle, Description, Download, ErrorIcon, Mail, Stethoscope, VerifiedUser, Visibility, Warning } from '@/shared/ui/icons';
import { useAccess, NO_ACCESS, hasPermission } from '@/shared/api/access';
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
import { Tooltip } from '@/shared/ui/Tooltip';
import { Checkbox } from '@/shared/ui/Checkbox';

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
      <div className="flex items-center gap-2 p-3 rounded-sm text-body-medium bg-success-container text-on-success-container">
        <Icon icon={CheckCircle} size={18} className="shrink-0" /> Проблем разрешения ссылок не найдено
      </div>
    );
  }
  const errors = diagnostics.filter(d => d.severity === 'error');
  const warnings = diagnostics.filter(d => d.severity === 'warning');
  return (
    <div className="rounded-sm border border-outline-variant overflow-hidden">
      <div className="px-3 py-2 text-body-small font-medium bg-surface-container-low text-on-surface-variant">
        Диагностика ссылок — объект «{objectName}»: {errors.length} ошиб., {warnings.length} предупр.
      </div>
      <div className="divide-y divide-surface-container max-h-72 overflow-y-auto">
        {[...errors, ...warnings].map((d, i) => (
          <div key={i} className="flex items-start gap-2 px-3 py-2 text-body-small">
            {d.severity === 'error'
              ? <Icon icon={ErrorIcon} size={18} className="text-error shrink-0 mt-0.5" />
              : <Icon icon={Warning} size={18} className="text-warning shrink-0 mt-0.5" />}
            <div className="min-w-0">
              <div className="text-on-surface-variant">Реквизит: <code className="text-on-surface-variant">{d.path}</code></div>
              <p className={d.severity === 'error' ? 'text-error' : 'text-on-surface-variant'}>{d.message}</p>
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
  // Кнопка отправки — по ПРАВУ, а не по имени роли (ТЗ ID-4.1, issue #989). Состав роли меняют в
  // редакторе, а имя остаётся прежним: проверка по имени означала, что носитель заведённой
  // администратором роли с правом рассылки кнопки не увидит, хотя сервер его пропускает.
  const { data: access = NO_ACCESS } = useAccess();
  const canSend = hasPermission(access, 'id.document.send');
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
      <div className={`p-3 rounded-sm text-body-medium ${STATUS_COLORS[instance.status] ?? 'bg-surface-container-low text-on-surface-variant'}`}>
        Статус: <strong>{STATUS_LABELS[instance.status] ?? instance.status}</strong>
        {instance.status === 'Generating' && <CircularProgress size={18} className="inline-block ml-2" />}
      </div>

      <div className="space-y-1">
        <label className="block text-body-small font-medium text-on-surface-variant">Шаблоны <span className="text-on-surface-variant font-normal">(можно несколько — по PDF на каждый)</span></label>
        {noTemplates ? (
          <p className="text-body-small text-warning">
            Для этого типа документа нет шаблонов. Создайте шаблон в разделе «Шаблоны».
          </p>
        ) : (
          <>
            {/* Клик по ВСЕЙ строке (обёртка-label) переключает участие в генерации (issue #316);
                фокус (показ параметров ниже) следует за кликом через onChange. Галочка отражает участие,
                подсветка строки — фокус. */}
            <div className="rounded-sm border border-outline divide-y divide-outline-variant overflow-hidden">
              {activeTemplates.map((t: Template) => {
                const selected = selectedTemplateIds.includes(t.id);
                const focused = focusedTemplate?.id === t.id;
                return (
                  <label key={t.id}
                    className={`state-layer flex items-center gap-2 pr-2.5 text-body-medium border-l-2 cursor-pointer ${focused ? 'bg-primary-container border-primary' : 'border-transparent'}`}>
                    <Checkbox size="sm" checked={selected} disabled={setTemplatesMutation.isPending}
                      onChange={v => { toggleTemplate(t.id, v); setChosenFocus(t.id); }}
                      aria-label={`Использовать шаблон «${t.name}» для генерации`}
                      boxClassName="ml-2.5" />
                    <span className="flex-1 min-w-0 py-1.5">
                      <span className={`truncate ${focused ? 'text-primary font-medium' : 'text-on-surface'}`}>
                        {t.isDefault ? '★ ' : ''}{t.name} <span className="text-on-surface-variant font-normal">(v{t.version})</span>
                      </span>
                    </span>
                  </label>
                );
              })}
            </div>
            {selectedTemplateIds.length === 0 && (
              <p className="text-label-small text-on-surface-variant">Ничего не выбрано — будет один PDF по шаблону по умолчанию.</p>
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
          loading={mutation.isPending} icon={<Icon icon={Description} size={18} />}>
          Сгенерировать PDF
        </Button>
        <Button variant="outlined" onClick={handleValidate} loading={validating}
          title="Проверить разрешение ссылок (каталог, наборы данных) без генерации"
          icon={<Icon icon={VerifiedUser} size={18} />}>
          Проверить ссылки
        </Button>
        <Button variant="outlined" onClick={() => setAuditOpen(true)}
          title="Найти в документе поля, которых нет в текущей схеме типа, и исправить"
          icon={<Icon icon={Stethoscope} size={18} />}>
          Аудит документа
        </Button>
        <InstanceAuditModal setId={setId} instanceId={instance.id} docName={instance.name || 'без названия'}
          schemaFieldKeys={schemaFieldKeys} open={auditOpen} onClose={() => setAuditOpen(false)} />
        <Button variant="outlined" onClick={handleDebugBundle} disabled={debugBusy || noTemplates}
          loading={debugBusy}
          title="Скачать ZIP с template.typ, data.json, typeblocks.typ и userlib.typ для отладки во внешнем инструменте (typst compile template.typ)"
          icon={<Icon icon={BugReport} size={18} />}>
          Отладочный пакет
        </Button>
      </div>
      {(mutation.isPending || instance.status === 'Generating') && (
        <p className="flex items-center gap-2 text-body-small text-on-surface-variant">
          <CircularProgress size={18} className="shrink-0" />
          Генерация PDF может занять несколько секунд — идёт сбор данных и компиляция Typst.
        </p>
      )}
      {/* whitespace-pre-line: отказ компилятора шаблона приходит несколькими строками — место
          ошибки отдельной строкой (issue #1047). Без этого они слипаются в один абзац. */}
      {error && <p className="text-body-medium text-error whitespace-pre-line">{error}</p>}

      {shownDiagnostics && <DiagnosticsPanel diagnostics={shownDiagnostics} objectName={instance.name || 'без названия'} />}
      {pdfFiles.length > 0 && (
        <div className="space-y-2">
          <div className="flex items-center justify-between">
            <p className="text-body-small font-medium text-on-surface-variant">Сгенерированные файлы</p>
            {canSend && (
              <Button title="Отправить сгенерированные PDF документа по почте (подписчикам и/или на внешние адреса)" variant="outlined" onClick={() => setEmailOpen(true)} icon={<Icon icon={Mail} size={18} />}>
                Отправить по почте
              </Button>
            )}
          </div>
          <div className="space-y-1.5">
            {pdfFiles.map(f => {
              const tpl = templates.find((t: Template) => t.id === f.templateId);
              return (
                <div key={f.id} className="flex items-center gap-2">
                  <Tooltip content={tpl?.name}><span className="text-body-small text-on-surface-variant flex-1 min-w-0 truncate">{tpl ? tpl.name : 'PDF'}</span></Tooltip>
                  {/* Во время генерации файл перезаписывается — блокируем открытие/скачивание, чтобы
                      не открыть неактуальную/заменяемую версию (ссылка ведёт на старое состояние).
                      Отказ из открытия доходит один — вкладку не дали открыть: об отказе загрузки
                      говорит сама вкладка. */}
                  <Button variant="outlined" disabled={busy} icon={<Icon icon={Visibility} size={18} />}
                    onClick={() => { setError(''); previewGeneratedFile(instance.id, f.templateId).catch((e: Error) => setError(e.message)); }}>
                    Открыть
                  </Button>
                  <Button variant="outlined" disabled={busy} icon={<Icon icon={Download} size={18} />}
                    onClick={() => downloadGeneratedFile(instance.id, f.templateId)}>
                    Скачать
                  </Button>
                </div>
              );
            })}
          </div>
        </div>
      )}

      {canSend && (
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
