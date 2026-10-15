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
import { RereadFailedNotice } from '@/shared/ui/RereadFailedNotice';
import { useGuardedNavigate, useLeaveGuard } from '@/shared/ui/NavigationGuard';
import { Select, SelectItem } from '@/shared/ui/Select';
import { useToast } from '@/shared/ui/Toast';
import { apiError } from '@/shared/utils/apiError';
import {
  NOT_ASSIGNED, changedRows, droppedNotice, fieldLabel, fullSentence, kindsText, listedFields, offeredFields,
  reconciled, selectedKey, withPick, withTypeTag,
  type MappingDraft, type RawSchema,
} from './fieldMapping';
import { saveGroup } from './fieldMappingSave';
import { PageHeader } from '@/shared/ui/PageHeader';

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
  const { data, isError, error, isFetching, refetch } = useFieldMapping();

  // Отказ во весь экран — только пока ответа не было НИ РАЗУ. Упавшее перечитывание оставляет
  // прежний ответ, а состояние запроса уже «ошибка»: проверь мы её раньше данных, экран снёс бы
  // сам себя вместе с несохранённым выбором и строкой отказа — в том числе с единственным
  // сообщением «сохранено не всё» (проверка правок PR #1319). А перечитывается экран после каждого
  // сохранения и с каждым возвратом в окно.
  // Пока ответа нет, строк нет: «не назначено» на месте неполученного было бы неправдой.
  if (!data)
    return isError ? (
      <Frame title="Сопоставление полей">
        <div className="flex items-center gap-3">
          <span className="text-sm text-danger">Сопоставление полей не загрузилось.</span>
          <Button variant="outlined" size="sm" onClick={() => void refetch()}>Повторить</Button>
        </div>
      </Frame>
    ) : <div className="px-6 py-10 text-center text-fg4 text-sm">Загрузка...</div>;

  if (data.length === 0)
    return (
      <Frame title="Сопоставление полей">
        <p className="text-sm text-fg3">Включённые модули из справочников ничего не читают — назначать нечего.</p>
      </Frame>
    );

  return (
    <Mapping data={data}
      reread={isError ? { error, retrying: isFetching, retry: () => void refetch() } : null} />
  );
}

/** Перечитывание не удалось: на экране прежний ответ. */
interface FailedReread {
  error: unknown;
  retrying: boolean;
  retry: () => void;
}

/** Состояние одной группы на экране; ключ — `groupId`. */
interface GroupEdit {
  draft: MappingDraft;
  refusal: string | null;
}

const UNTOUCHED: GroupEdit = { draft: {}, refusal: null };

function groupId(module: ModuleCoverage, group: CoverageGroup): string {
  return `${module.code}:${group.role}`;
}

/**
 * Экран с данными. Черновики всех групп живут ЗДЕСЬ, а не в группах: охрана ухода в приложении одна
 * на страницу, и группа, зарегистрировавшая свою, стирала бы чужую — правки первой группы терялись
 * бы молча, стоило сохранить или отменить вторую (ревью PR #1318).
 */
function Mapping({ data, reread: failed }: { data: ModuleCoverage[]; reread: FailedReread | null }) {
  const qc = useQueryClient();
  const update = useUpdateDocumentTypeSchema();
  const toast = useToast();
  const [edits, setEdits] = useState<Record<string, GroupEdit>>({});
  const [saving, setSaving] = useState(false);
  const [leave, setLeave] = useState<(() => void) | null>(null);

  // Пришёл другой ответ — сверяем с ним черновики: выбор, которого список больше не предлагает,
  // снимается из состояния, а не прячется при чтении, и группа говорит об этом словами. Сверка идёт
  // прямо в рендере (приём «состояние по смене входа»): так кадра с недействительным выбором на
  // экране не бывает вовсе. Перечитывание приходит не только после сохранения — ещё с возвратом
  // в окно и после правок соседней группы.
  const [seen, setSeen] = useState(data);
  if (seen !== data) {
    setSeen(data);
    const next = { ...edits };
    let lost = false;
    for (const module of data) for (const group of module.groups) {
      const id = groupId(module, group);
      const edit = edits[id];
      if (!edit) continue;
      const { draft, dropped } = reconciled(group, edit.draft);
      if (draft === edit.draft) continue;
      lost = true;
      // О снятом выборе говорим ВСЕГДА, и при стоящем отказе сохранения — дописываем к нему, а
      // не уступаем: отказ мог быть по другой причине или про другую строку, и тогда выбор исчез
      // бы молча — ровно то, от чего эта сверка заведена.
      const notice = droppedNotice(group, dropped);
      next[id] = { draft, refusal: edit.refusal ? `${edit.refusal} ${notice}` : notice };
    }
    if (lost) {
      setEdits(next);
      // Диалог ухода закрываем: он предлагал сохранить то, чего больше нет, и «Сохранить» в нём
      // прошло бы по пустому списку и увело со страницы, не сохранив ничего и ничего не сказав.
      setLeave(null);
    }
  }

  const dirty = data.flatMap(m => m.groups.map(g => ({ id: groupId(m, g), group: g })))
    .filter(x => changedRows(x.group, edits[x.id]?.draft ?? {}).length > 0);
  useLeaveGuard(dirty.length > 0, proceed => setLeave(() => proceed));

  /** Перечитать всё, что зависит от схемы: и после отказа — тип могли тем временем изменить. */
  const reread = () => Promise.all([
    qc.invalidateQueries({ queryKey: FIELD_MAPPING_KEY }),
    qc.invalidateQueries({ queryKey: MODULE_SETTINGS_KEY }),
  ]);

  const set = (id: string, edit: GroupEdit) => setEdits(all => ({ ...all, [id]: edit }));

  async function save(id: string, group: CoverageGroup): Promise<boolean> {
    setSaving(true);
    try {
      const outcome = await saveGroup(group, edits[id]?.draft ?? {}, {
        read: async typeId => {
          const type = await freshType(typeId);
          return { name: type.name, schema: type.schema as unknown as RawSchema };
        },
        write: async (typeId, schema) => { await update.mutateAsync({ id: typeId, schema: JSON.stringify(schema) }); },
      });
      if (outcome.ok) {
        set(id, UNTOUCHED);
        toast.success('Сопоставление полей сохранено.');
        return true;
      }
      // Постоянной строкой, а не только тостом: тост исчезает, а причина нужна, пока её не устранили.
      // Сохранилось частично — черновик сбрасываем: он собран по состоянию, которого больше нет.
      const reason = fullSentence(apiError(outcome.error, 'Сервер не ответил'));
      // Черновик берём из состояния на этот миг, а не из замыкания: пока шла запись, экран мог
      // перечитаться и снять из него выбор, которого больше нет, — замыкание вернуло бы его обратно.
      setEdits(all => ({
        ...all,
        [id]: outcome.partial
          ? { draft: {}, refusal: `Сохранено не всё, и вернуть прежнее назначение не удалось. ${reason} Проверьте строки ниже.` }
          : { draft: all[id]?.draft ?? {}, refusal: `Не сохранено, назначение осталось прежним. ${reason}` },
      }));
      return false;
    } finally {
      setSaving(false);
      await reread();
    }
  }

  const single = data.length === 1 ? data[0] : null;
  return (
    <Frame title={single ? `Сопоставление полей — ${single.title}` : 'Сопоставление полей'}>
      {failed && (
        <RereadFailedNotice what="Сопоставление полей не перечиталось"
          error={failed.error} retrying={failed.retrying} onRetry={failed.retry} />
      )}
      <p className="text-xs text-fg3 max-w-2xl">
        Модуль берёт эти сведения из справочников. Здесь назначается, какой тип служит справочником и какое
        его поле за что отвечает. То же самое видно в редакторе типа — тэгом типа и тэгом поля.
      </p>
      {data.map(module => (
        <section key={module.code} className="space-y-5" data-module={module.code}>
          {!single && <h2 className="text-base font-semibold text-fg1">{module.title}</h2>}
          {module.unmet > 0 && (
            <p className="text-sm text-warning">
              Не назначено {module.unmet} из {module.total} — считая справочники и их обязательные поля.
            </p>
          )}
          {module.groups.map(group => {
            const id = groupId(module, group);
            return (
              <GroupSection key={id} group={group} edit={edits[id] ?? UNTOUCHED} saving={saving}
                onEdit={edit => set(id, edit)} onSave={() => void save(id, group)} onAssigned={reread} />
            );
          })}
        </section>
      ))}

      <LeaveGuardDialog
        open={leave !== null} saving={saving}
        onCancel={() => setLeave(null)}
        onDiscard={() => { const proceed = leave; setLeave(null); setEdits({}); proceed?.(); }}
        onSave={async () => {
          // Сохраняем ВСЕ группы с правками. Не сохранилась хоть одна — остаёмся: причина под ней.
          const proceed = leave;
          setLeave(null);
          for (const { id, group } of dirty) if (!(await save(id, group))) return;
          proceed?.();
        }} />
    </Frame>
  );
}

function Frame({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="px-6 py-4 max-w-5xl space-y-5">
      <PageHeader title={title} />
      {children}
    </div>
  );
}

/** Свежая схема типа — прямо перед записью: список на экране мог устареть. */
async function freshType(id: string): Promise<DocumentType> {
  return (await apiClient.get<DocumentType>(`/document-types/${id}`)).data;
}

/** Одна группа — справочник. Состояние и запись держит экран: см. `Mapping`. */
function GroupSection({ group, edit, saving, onEdit, onSave, onAssigned }: {
  group: CoverageGroup; edit: GroupEdit; saving: boolean;
  onEdit: (edit: GroupEdit) => void; onSave: () => void; onAssigned: () => Promise<unknown>;
}) {
  const go = useGuardedNavigate();
  const { draft, refusal } = edit;
  // Считаем раз на группу и раздаём строкам признаком: каждая строка пересчитывала бы всю группу.
  const changed = new Set(changedRows(group, draft));
  const dirty = changed.size > 0;

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

      {group.state === 'absent' && <AssignRole group={group} onDone={onAssigned} />}

      {group.state === 'violated' && (
        <div className="space-y-2">
          <p className="text-sm text-danger max-w-3xl">
            <b>Нарушено.</b> {fullSentence(group.reason ?? '')} {group.withoutIt}{' '}
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
            <Row key={row.tag} row={row} typeName={group.type?.name ?? ''} draft={draft}
              changed={changed.has(row)} disabled={saving}
              onPick={key => onEdit({ draft: withPick(group, draft, row, key), refusal: null })} />
          ))}
        </div>
      )}

      {refusal && (
        <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1 max-w-3xl">
          <p className="text-sm text-danger" role="alert">{refusal}</p>
          {/* Правок нет — «Отменить» скрыта, и убрать прочитанное сообщение было бы нечем. */}
          {!dirty && <Button variant="text" size="sm" onClick={() => onEdit(UNTOUCHED)}>Понятно</Button>}
        </div>
      )}

      {/* Кнопки — только когда есть что сохранять: недоступное действие не показываем. */}
      {dirty && (
        <div className="flex items-center gap-3 pt-1">
          <Button variant="filled" onClick={onSave} disabled={saving}>Сохранить</Button>
          <Button variant="text" onClick={() => onEdit(UNTOUCHED)} disabled={saving}>Отменить</Button>
        </div>
      )}
    </div>
  );
}

function Row({ row, typeName, draft, changed, disabled, onPick }: {
  row: CoverageRow; typeName: string; draft: MappingDraft; changed: boolean; disabled: boolean;
  onPick: (key: string | null) => void;
}) {
  const listed = listedFields(row);
  // Тем же расчётом, которым логика решает, действителен ли выбор: список есть ровно тогда, когда
  // в нём есть что выбрать.
  const selectable = offeredFields(row).length > 0;

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
      <b>Нарушено.</b> {fullSentence(row.reason ?? '')} {row.withoutIt} {way}
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
  // Выбор действителен, пока тип есть среди предлагаемых: список перечитывается, и тип, только что
  // назначенный другим справочником, из него уходит. Запомненный выбор оставил бы кнопку, которая
  // поставила бы ему вторую роль (ревью PR #1318). Снимаем его из состояния, а не прячем при
  // чтении — как и выбор поля: спрятанный ожил бы, вернись тип в список.
  if (typeId !== undefined && !group.candidates.some(c => c.id === typeId)) setTypeId(undefined);
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
      setRefusal('Не назначено. ' + fullSentence(apiError(e, 'Сервер не ответил')));
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
