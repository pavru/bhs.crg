import { Icon } from '@/shared/ui/Icon';
import { DoneAll } from '@/shared/ui/icons';
import { Button } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { DateField } from '@/shared/ui/DateField';
import { Select, SelectItem } from '@/shared/ui/Select';
import { withArchiveWord } from '@/shared/ui/archive';
import type { CostsOrganization, InvoiceView } from '@/shared/api/invoices';
import {
  FIELD_LABEL, K, UNCONFIRMED_HINT, asInput, catalogRef, fromInput, isMarked, moneyInput, refEntryId, unconfirmedInBlock,
  type InvoiceBlock,
} from './invoiceFields';
import { READINESS_ATTR, isRequired, readinessAnchor } from './invoiceReadiness';
import { LOST } from './lostReferences';
import { InvoiceFieldScan } from './InvoiceFieldScan';
import { InvoiceDueField } from './InvoiceDueField';
import { deferralNote } from './invoiceDue';
import { useCorrecting } from './useCorrectionDraft';
import { ExtraField } from './InvoiceExtraFields';
import { useExtraField } from './invoiceExtras';

/**
 * Поля шапки счёта — блок полей части и одно поле (вынесены из `InvoiceForm`, costs-invoice-screen-10-layout:
 * форма собирает сводку и части, а что стоит в каждом поле — здесь).
 */

/**
 * Поля блока — сеткой по ширине части, а не окна (`@container`): рядом с файлом часть узка так же, как на
 * узком экране. У «Кто и сколько» стороны — по половине, номер, дата и суммы — по четверти (как в Г).
 */
export function BlockFields({
  block, view, edits, organizations, organizationsUnread, value, set, locked,
}: {
  block: InvoiceBlock; locked: boolean;
  view: InvoiceView; edits: Record<string, unknown>; organizations: CostsOrganization[];
  organizationsUnread: boolean;
  value: (key: string) => unknown; set: (key: string, next: unknown) => void;
}) {
  // В исправлении поля открыты, но то, что пишет само (прочитанное со скана), — нет.
  const correcting = useCorrecting() !== null;
  const quiet = locked || correcting;
  const wide = (key: string) => key === K.supplier || key === K.payer;

  return (
    <div className="@container">
      <div className="grid grid-cols-1 @lg:grid-cols-2 @3xl:grid-cols-12 gap-3">
        {block.fields.map(key => (
          <div key={key} className={`min-w-0 ${block.id === 'who' ? (wide(key) ? '@3xl:col-span-6' : '@3xl:col-span-3') : '@3xl:col-span-4'}`}
            {...{ [READINESS_ATTR]: readinessAnchor({ place: 'field', key }) }}>
            <Field fieldKey={key} view={view} edits={edits} organizations={organizations}
              organizationsUnread={organizationsUnread} value={value} set={set} locked={locked} />
            {!quiet && (
              <InvoiceFieldScan fieldKey={key} view={view} edits={edits} set={set} organizations={organizations} />
            )}
          </div>
        ))}
      </div>
    </div>
  );
}

/**
 * «Всё верно (N)» — в шапке части: снимает метки «со скана» с полей её блоков. Кнопки нет, когда снимать
 * нечего: пустой перечень сервер отвергает, и предлагать действие, которое заведомо откажет, — обман.
 */
export function ConfirmBlock({ blocks, view, locked, confirming, onConfirm }: {
  blocks: readonly InvoiceBlock[]; view: InvoiceView; locked: boolean; confirming: boolean;
  onConfirm: (fields: string[]) => void;
}) {
  const correcting = useCorrecting() !== null;
  const marks = blocks.flatMap(block => unconfirmedInBlock(block, view.unconfirmed));
  if (marks.length === 0 || locked || correcting) return null;
  return (
    <Button variant="text" icon={<Icon icon={DoneAll} size={18} />} loading={confirming} onClick={() => onConfirm(marks)}>
      Всё верно ({marks.length})
    </Button>
  );
}

function Field({ fieldKey, view, edits, organizations, organizationsUnread, value, set, locked }: {
  fieldKey: string; view: InvoiceView; edits: Record<string, unknown>;
  /** Счёт заперт закрытым периодом: поля только читаются. Читаются, а не отключены — приглушённое
   *  поле выглядит пустым, а причину называет полоса вверху. */
  locked: boolean;
  organizations: CostsOrganization[]; organizationsUnread: boolean;
  value: (key: string) => unknown; set: (key: string, next: unknown) => void;
}) {
  // Метка «со скана, не проверено» — знаком в хвосте поля (`fromScan`, [UI-88], D-11.4): ни рамки, ни строки под
  // полем. Сверяют по файлу: «Показать на скане» — у пункта перечня о метках, не в поле (вариант А).
  const marked = isMarked(fieldKey, view.unconfirmed, edits);
  // Обязательное для «Разобран» — пометкой у подписи (issue #1291). Перечень присылает сервер.
  const required = isRequired(view, fieldKey);
  const current = value(fieldKey);
  const extra = useExtraField(fieldKey);

  // Поле, дописанное в тип, рисует общий с документами `PrimitiveInput`: знака у него нет, и метка — прежней рамкой
  // предупреждения (не ошибки: распознанное значение не ошибка, его просто никто пока не подтвердил) с подсказкой.
  if (extra) return <ExtraField field={extra} value={current} set={set} locked={locked}
    hint={marked ? UNCONFIRMED_HINT : undefined} frame={marked ? 'rounded-sm ring-1 ring-warning' : ''} />;

  switch (fieldKey) {
    case K.supplier:
    case K.payer: {
      const entryId = refEntryId(current);

      // Ссылка есть, а записи нет — организацию удалили. Radix показал бы такое значение
      // ПЛЕЙСХОЛДЕРОМ «Выберите организацию», то есть соврал бы: поле выглядело бы незаполненным, и
      // человек, ничего не трогая, сохранил бы счёт со ссылкой в пустоту. Реестр в том же случае
      // честно пишет «организация удалена» — форма обязана говорить то же самое.
      //
      // Потерю называет СЕРВЕР (issue #1184), и только у значения, которое лежит в счёте: сравнение со
      // списком организаций показывало «удалена» у живого поставщика, пока список грузится. Старый
      // сервер состояния не присылает — тогда судим по списку, как раньше.
      const side = fieldKey === K.supplier ? 'supplier' : 'payer';
      const state = view.references?.[side];
      const storedId = refEntryId(view.requisites[fieldKey]);
      const stored = entryId !== null && entryId === storedId;
      // Организация, стоящая в счёте, в архиве (issue #1185). В списке на выбор её нет, название
      // едет с самим счётом. Пункт держится за СОХРАНЁННЫМ значением, а не за текущим: заменил,
      // передумал — вернул; сервер стоявшую ссылку принимает. После сохранения с другой он исчезнет.
      const kept = state === 'archived' && storedId !== null
        ? { id: storedId, name: withArchiveWord(view.references?.[`${side}Name`] ?? 'организация', true) }
        : null;
      const archived = kept !== null && entryId === kept.id && !organizations.some(o => o.id === kept.id);
      const lost = entryId !== null && (view.references
        ? stored && state === 'lost'
        : !organizationsUnread && !organizations.some(o => o.id === entryId));
      // Запись есть, но в списке организаций её нет — перевели в другой вид. Не потеря, но и молчать
      // нельзя: без своего пункта Radix показал бы плейсхолдер, и поле со ссылкой выглядело бы пустым
      // (ревью PR #1211). Пока список не пришёл (он пуст), судить не о чем.
      const foreign = entryId !== null && !lost && !archived
        && organizations.length > 0 && !organizations.some(o => o.id === entryId);
      const title = fieldKey === K.supplier ? 'Поставщик' : 'Плательщик';

      // Запертый счёт: выбор заменён полем для чтения, как у остальных, — отключённый выбор приглушён
      // и читается как «здесь ничего нет».
      if (locked)
        return (
          <TextField label={title} readOnly onChange={() => {}}
            value={organizationsUnread ? 'справочник не прочитан'
              : entryId === null ? '' : archived ? kept.name
              : organizations.find(o => o.id === entryId)?.name ?? (lost ? LOST.organization : foreign ? LOST.movedOrganization : '')} />
        );

      return (
        <Select label={title} required={required} fromScan={marked}
          disabled={organizationsUnread}
          hint={organizationsUnread ? 'Справочник не прочитан — выбор недоступен'
            : lost ? 'Запись справочника удалена. Счёт сохраняется и так; исправить — выбрать другую организацию'
            : foreign ? 'Запись есть, но она больше не организация. Выберите организацию заново'
            : archived ? 'Запись в архиве. Счёт сохраняется как есть; после замены выбрать её заново будет нельзя'
            : undefined}
          value={entryId ?? NOT_CHOSEN} placeholder="Выберите организацию"
          onValueChange={id => set(fieldKey, id === NOT_CHOSEN ? null : catalogRef(id))}>
          {/* Пункт «не выбрано» — единственный способ СНЯТЬ ссылку: пустое значение Radix не
              отдаёт, и без него ошибочно распознанный плательщик оставался бы в записи навсегда. */}
          <SelectItem value={NOT_CHOSEN}>— не выбрано —</SelectItem>
          {lost && <SelectItem value={entryId}>{LOST.organization}</SelectItem>}
          {foreign && <SelectItem value={entryId}>{LOST.movedOrganization}</SelectItem>}
          {/* Список и счёт читаются порознь и могут разойтись на миг (организацию только что
              вернули): два пункта с одним значением Radix отметил бы выбранными оба. */}
          {kept && !organizations.some(o => o.id === kept.id) && <SelectItem value={kept.id}>{kept.name}</SelectItem>}
          {organizations.map(o => <SelectItem key={o.id} value={o.id}>{o.name}</SelectItem>)}
        </Select>
      );
    }

    case K.dueDate:
      return <InvoiceDueField view={view} edits={edits} current={current} set={set} locked={locked}
        fromScan={marked} required={required} />;

    case K.date:
    case K.shippedOn:
      // «Отгрузка раньше даты счёта» — пункт перечня состояния (11, 32), а не подсказка у поля.
      return (
        <DateField label={LABELS[fieldKey]} required={required} value={asInput(current).slice(0, 10)}
          fromScan={marked}
          readOnly={locked}
          onChange={iso => set(fieldKey, iso ? iso.slice(0, 10) : null)} />
      );

    default: {
      const money = fieldKey === K.total || fieldKey === K.vat;
      const numeric = money || fieldKey === K.deferral;
      const label = LABELS[fieldKey] ?? fieldKey;
      // У отсрочки — взята ли она из условий поставщика (ТЗ COST-9.2): иначе подставленное число
      // выглядело бы вписанным руками, а расхождение с условиями — незаметным.
      return (
        <TextField label={label} readOnly={locked} required={required}
          hint={fieldKey === K.deferral ? deferralNote(view, edits) ?? undefined : undefined}
          value={money ? moneyInput(current) : asInput(current)}
          inputMode={numeric ? 'decimal' : undefined}
          fromScan={marked}
          onChange={e => set(fieldKey, fromInput(e.target.value))} />
      );
    }
  }
}

/** Значение пункта «не выбрано»: пустую строку Radix не принимает, а ссылку снимать чем-то надо. */
const NOT_CHOSEN = 'не-выбрано';

const LABELS = FIELD_LABEL;
