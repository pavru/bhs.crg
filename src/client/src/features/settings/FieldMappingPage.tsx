import { useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/shared/api/client';
import { useUpdateDocumentTypeSchema } from '@/shared/api/documentTypes';
import {
  FIELD_MAPPING_KEY, useFieldMapping,
  type CoverageGroup, type CoverageRow, type ModuleCoverage,
} from '@/shared/api/fieldMapping';
import { MODULE_SETTINGS_KEY } from '@/shared/api/moduleSettings';
import type { DocumentType } from '@/shared/api/types';
import { Button } from '@/shared/ui/Button';
import { LeaveGuardDialog } from '@/shared/ui/LeaveGuardDialog';
import { useGuardedNavigate, useLeaveGuard } from '@/shared/ui/NavigationGuard';
import { Select, SelectItem } from '@/shared/ui/Select';
import { useToast } from '@/shared/ui/Toast';
import { apiError } from '@/shared/utils/apiError';
import {
  NOT_ASSIGNED, changedRows, fieldLabel, kindsText, listedFields, missingKey, schemaWrites, selectedKey,
  withTagPlacements, withTypeTag,
  type MappingDraft, type RawSchema,
} from './fieldMapping';

/**
 * «Сопоставление полей» (ТЗ TYPE-23.1, issue #1101): что включённые модули читают из справочников
 * ядра, чем это обеспечено и что не работает, пока не обеспечено.
 *
 * Назначение — тэг типа или поля; то же самое видно и правится в редакторе типа. Запись идёт его же
 * адресом (сохранение схемы), по справочнику: группа — одно сохранение. Своего пишущего адреса у
 * экрана нет нарочно — иначе у схемы было бы два входа с разными проверками.
 *
 * Состояния считает сервер тем же расчётом, которым справочник читает модуль: «назначено» здесь
 * значит «модуль это поле находит», а не «тэг где-то стоит».
 */
export function FieldMappingPage() {
  const { data, isError, refetch } = useFieldMapping();

  // Пока ответа нет, строк нет: «не назначено» на месте неполученного было бы неправдой.
  if (isError)
    return (
      <Frame title="Сопоставление полей">
        <div className="flex items-center gap-3">
          <span className="text-sm text-danger">Сопоставление полей не загрузилось.</span>
          <Button variant="outlined" size="sm" onClick={() => void refetch()}>Повторить</Button>
        </div>
      </Frame>
    );
  if (!data) return <div className="px-6 py-10 text-center text-fg4 text-sm">Загрузка...</div>;

  if (data.length === 0)
    return (
      <Frame title="Сопоставление полей">
        <p className="text-sm text-fg3">Включённые модули из справочников ничего не читают — назначать нечего.</p>
      </Frame>
    );

  const single = data.length === 1 ? data[0] : null;
  return (
    <Frame title={single ? `Сопоставление полей — ${single.title}` : 'Сопоставление полей'}>
      <p className="text-xs text-fg3 max-w-2xl">
        Модуль берёт эти сведения из справочников. Здесь назначается, какой тип служит справочником и какое
        его поле за что отвечает. То же самое видно в редакторе типа — тэгом типа и тэгом поля.
      </p>
      {data.map(module => <ModuleBlock key={module.code} module={module} titled={!single} />)}
    </Frame>
  );
}

function Frame({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="px-6 py-4 max-w-5xl space-y-5">
      <h1 className="text-xl font-semibold text-fg1">{title}</h1>
      {children}
    </div>
  );
}

function ModuleBlock({ module, titled }: { module: ModuleCoverage; titled: boolean }) {
  return (
    <section className="space-y-5" data-module={module.code}>
      {titled && <h2 className="text-base font-semibold text-fg1">{module.title}</h2>}
      {module.unmet > 0 && (
        <p className="text-sm text-warning">Не назначено: {module.unmet} из {module.total}.</p>
      )}
      {module.groups.map(group => <GroupSection key={group.role} group={group} />)}
    </section>
  );
}

/** Первая буква — заглавная, в конце точка: причину сервер отдаёт серединой фразы. */
function sentence(text: string): string {
  const t = text.trim();
  return t.charAt(0).toUpperCase() + t.slice(1) + (/[.!?]$/.test(t) ? '' : '.');
}

/** Свежая схема типа — прямо перед записью: список на экране мог устареть. */
async function freshType(id: string): Promise<DocumentType> {
  return (await apiClient.get<DocumentType>(`/document-types/${id}`)).data;
}

function GroupSection({ group }: { group: CoverageGroup }) {
  const go = useGuardedNavigate();
  const qc = useQueryClient();
  const update = useUpdateDocumentTypeSchema();
  const toast = useToast();
  const [draft, setDraft] = useState<MappingDraft>({});
  const [refusal, setRefusal] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [leave, setLeave] = useState<(() => void) | null>(null);

  const dirty = changedRows(group, draft).length > 0;
  useLeaveGuard(dirty, proceed => setLeave(() => proceed));

  /** Перечитать всё, что зависит от схемы: и после отказа — тип могли тем временем изменить. */
  const reread = () => Promise.all([
    qc.invalidateQueries({ queryKey: FIELD_MAPPING_KEY }),
    qc.invalidateQueries({ queryKey: MODULE_SETTINGS_KEY }),
  ]);

  /**
   * Записать группу. Правка — по одной на тип; сначала те, что только СНИМАЮТ тэг: сервер считает
   * «одно поле типа на тэг» по типу вместе с предками, и постановка раньше снятия была бы отказом.
   */
  async function save(): Promise<boolean> {
    const writes = schemaWrites(group, draft)
      .sort((a, b) => Number(a.placements.some(p => p.key !== null)) - Number(b.placements.some(p => p.key !== null)));
    let written = 0;
    setSaving(true);
    try {
      for (const write of writes) {
        const type = await freshType(write.typeId);
        const schema = type.schema as unknown as RawSchema;
        const gone = missingKey(schema, write.placements);
        if (gone !== null)
          throw new Error(`В типе «${type.name}» больше нет поля «${gone}» — тип тем временем изменили. ` +
            'Список полей обновлён, выберите заново.');
        const next = withTagPlacements(schema, write.placements);
        if (next === schema) continue;
        await update.mutateAsync({ id: write.typeId, schema: JSON.stringify(next) });
        written++;
      }
      setDraft({});
      setRefusal(null);
      toast.success('Сопоставление полей сохранено.');
      return true;
    } catch (e) {
      // Постоянной строкой, а не только тостом: тост исчезает, а причина нужна, пока её не устранили.
      setRefusal((written > 0 ? 'Сохранено не всё. ' : 'Не сохранено. ') + sentence(apiError(e, 'Сервер не ответил')));
      if (written > 0) setDraft({});
      return false;
    } finally {
      setSaving(false);
      await reread();
    }
  }

  return (
    <div className="space-y-2" data-role={group.role}>
      <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
        <h3 className="text-sm font-semibold text-fg1">{group.label}</h3>
        {group.type && (
          <>
            <span className="text-sm text-fg2">тип «{group.type.name}»</span>
            <Button variant="text" size="sm" onClick={() => go(`/composite-types?type=${group.type!.id}`)}>
              Открыть тип
            </Button>
          </>
        )}
        {dirty && <span className="text-xs text-warning">не сохранено</span>}
      </div>
      <p className="text-xs text-fg3">{group.purpose}.</p>

      {group.state === 'absent' && <AssignRole group={group} onDone={reread} />}

      {group.state === 'violated' && (
        <div className="space-y-2">
          <p className="text-sm text-danger max-w-3xl">
            <b>Нарушено.</b> {sentence(group.reason ?? '')} {group.withoutIt}{' '}
            Исправляется в редакторе типов: тэг типа «{group.label}» должен остаться у одного составного типа.
          </p>
          <Button variant="outlined" size="sm" onClick={() => go('/composite-types')}>Открыть составные типы</Button>
        </div>
      )}

      {group.state === 'mapped' && group.rows.length > 0 && (
        <div className="border-t border-stroke">
          <div className="grid grid-cols-[minmax(0,35fr)_minmax(0,30fr)_minmax(0,35fr)] gap-x-4 py-1.5 text-xs text-fg3">
            <span>Что нужно модулю</span><span>Поле</span><span>Состояние</span>
          </div>
          {group.rows.map(row => (
            <Row key={row.tag} row={row} group={group} draft={draft} disabled={saving}
              onPick={key => { setDraft(d => ({ ...d, [row.tag]: key })); setRefusal(null); }} />
          ))}
        </div>
      )}

      {refusal && <p className="text-sm text-danger max-w-3xl" role="alert">{refusal}</p>}

      {/* Кнопки — только когда есть что сохранять: недоступное действие не показываем. */}
      {dirty && (
        <div className="flex items-center gap-3 pt-1">
          <Button variant="filled" onClick={() => void save()} disabled={saving}>Сохранить</Button>
          <Button variant="text" onClick={() => { setDraft({}); setRefusal(null); }} disabled={saving}>Отменить</Button>
        </div>
      )}

      <LeaveGuardDialog
        open={leave !== null} saving={saving}
        onCancel={() => setLeave(null)}
        onDiscard={() => { const proceed = leave; setLeave(null); setDraft({}); proceed?.(); }}
        onSave={async () => {
          // Не сохранилось — остаёмся: причина стоит под группой.
          const proceed = leave;
          setLeave(null);
          if (await save()) proceed?.();
        }} />
    </div>
  );
}

function Row({ row, group, draft, disabled, onPick }: {
  row: CoverageRow; group: CoverageGroup; draft: MappingDraft; disabled: boolean;
  onPick: (key: string | null) => void;
}) {
  const listed = listedFields(row);
  const selectable = listed.some(f => f.fits);
  const changed = changedRows(group, draft).includes(row);
  const typeName = group.type?.name ?? '';

  return (
    <div className="grid grid-cols-[minmax(0,35fr)_minmax(0,30fr)_minmax(0,35fr)] gap-x-4 py-2.5 border-t border-stroke items-start"
      data-tag={row.tag}>
      <div>
        {/* Код тэга — подсказкой: он нужен разработчику, а не администратору. */}
        <p className="text-sm text-fg1" title={row.tag}>
          {row.label}{!row.required && <span className="text-fg3"> · необязательно</span>}
        </p>
        <p className="text-xs text-fg3 mt-0.5">{row.description}</p>
      </div>

      <div>
        {row.fault === 'DiffersInSubtype' ? (
          <p className="text-sm text-fg2">{listed.find(f => f.tagged)?.title ?? '—'}</p>
        ) : selectable ? (
          <Select
            aria-label={`Поле для «${row.label}»`}
            value={selectedKey(row, draft)}
            onValueChange={v => onPick(v === NOT_ASSIGNED ? null : v)}
            placeholder={row.fault === 'Several' ? 'Выберите одно' : 'Выберите поле'}
            disabled={disabled}
            className={row.state !== 'mapped' && row.required && !changed ? 'border-warning' : ''}
          >
            {/* Обязательное можно заменить, но не снять: снять его здесь значило бы остановить модуль
                одной прокруткой списка. Кому это правда нужно, снимет тэг в редакторе типа. */}
            {!row.required && <SelectItem value={NOT_ASSIGNED}>Не назначено</SelectItem>}
            {listed.map(field => (
              <SelectItem key={field.key} value={field.key} disabled={!field.fits}>
                {fieldLabel(field, row.fields)}
                {!field.fits ? ' — не подходит' : field.tagged && row.fault === 'Several' ? ' · тэг стоит' : ''}
              </SelectItem>
            ))}
          </Select>
        ) : (
          // Пустой список был бы правдоподобным пустым ответом — говорим, чего в типе нет.
          <p className="text-sm text-fg2">
            В типе «{typeName}» нет поля {row.kinds.length > 0 ? `вида ${kindsText(row)}` : 'подходящего вида'}.
          </p>
        )}
        {changed && <p className="text-xs text-warning mt-1">не сохранено</p>}
      </div>

      <RowState row={row} selectable={selectable} />
    </div>
  );
}

/** Состояние строки. У назначенной — пусто: норма не шумит, и «работает» экран не знает. */
function RowState({ row, selectable }: { row: CoverageRow; selectable: boolean }) {
  if (row.state === 'mapped') return <div />;

  if (row.state === 'absent')
    return (
      <p className={`text-sm ${row.required ? 'text-warning' : 'text-fg3'}`}>
        {row.required ? <b>Не назначено.</b> : 'Не назначено.'} {row.withoutIt}
        {!selectable && ' Добавьте в тип поле подходящего вида — оно появится в этом списке.'}
      </p>
    );

  const way = row.fault === 'Several'
    ? 'Выберите одно — с остальных назначение снимется при сохранении.'
    : row.fault === 'WrongKind'
      ? 'Выберите другое поле или верните полю прежний вид в редакторе типа.'
      : 'Исправляется в редакторе типа: у подтипа тэг должен стоять на том же поле либо не стоять вовсе.';
  return (
    <p className="text-sm text-danger">
      <b>Нарушено.</b> {sentence(row.reason ?? '')} {row.withoutIt} {way}
    </p>
  );
}

/**
 * Справочник не назначен: выбор типа и назначение. Только здесь — пока роль не несёт никто;
 * перенести справочник с типа на тип нельзя, пока на его записи ссылаются документы модулей
 * (решение владельца 13.10.2026), и предлагать перенос экрану нечем.
 */
function AssignRole({ group, onDone }: { group: CoverageGroup; onDone: () => Promise<unknown> }) {
  const go = useGuardedNavigate();
  const update = useUpdateDocumentTypeSchema();
  const toast = useToast();
  const [typeId, setTypeId] = useState<string | undefined>(undefined);
  const [refusal, setRefusal] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  async function assign(id: string) {
    setSaving(true);
    try {
      const type = await freshType(id);
      const schema = type.schema as unknown as RawSchema;
      const next = withTypeTag(schema, group.role);
      if (next !== schema) await update.mutateAsync({ id, schema: JSON.stringify(next) });
      setRefusal(null);
      toast.success(`Тип «${type.name}» назначен: ${group.label.toLowerCase()}.`);
    } catch (e) {
      setRefusal('Не назначено. ' + sentence(apiError(e, 'Сервер не ответил')));
    } finally {
      setSaving(false);
      await onDone();
    }
  }

  return (
    <div className="space-y-2">
      <p className="text-sm text-danger max-w-3xl"><b>{group.label} не назначен.</b> {group.withoutIt}</p>
      {group.candidates.length === 0 ? (
        <>
          <p className="text-sm text-fg2">Заведите составной тип в разделе «Составные типы» — он появится здесь.</p>
          <Button variant="outlined" size="sm" onClick={() => go('/composite-types')}>Открыть составные типы</Button>
        </>
      ) : (
        <>
          <div className="flex flex-wrap items-center gap-3">
            <div className="w-72">
              <Select aria-label={`Тип — ${group.label.toLowerCase()}`}
                value={typeId} onValueChange={setTypeId} placeholder="Выберите тип" disabled={saving}>
                {group.candidates.map(c => <SelectItem key={c.id} value={c.id}>{c.name}</SelectItem>)}
              </Select>
            </div>
            {/* Кнопка — только когда тип выбран: недоступное действие не показываем. */}
            {typeId && (
              <Button variant="filled" onClick={() => void assign(typeId)} disabled={saving}>
                Назначить справочником
              </Button>
            )}
          </div>
          <p className="text-xs text-fg3 max-w-2xl">
            Подтипы выбранного типа войдут в справочник. Перенести справочник на другой тип потом нельзя,
            пока на его записи ссылаются документы модуля. Типы модуля, которые ждали этот справочник,
            появятся после перезапуска приложения.
          </p>
        </>
      )}
      {refusal && <p className="text-sm text-danger max-w-3xl" role="alert">{refusal}</p>}
    </div>
  );
}
