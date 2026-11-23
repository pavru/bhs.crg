import type { InvoiceReadinessGap, PaymentView, PaymentRequisiteGap } from '@/shared/api/invoices';
import { FIELD_LABEL, K } from './invoiceFields';
import type { StatusBlock, StatusItem } from './invoiceStatus';

export function matchesPaymentGap(gap: PaymentRequisiteGap, readiness: InvoiceReadinessGap): boolean {
  return readiness.field === gap.field && readiness.kind === (gap.kind === 'missing' ? 'field' : 'reference');
}

/** В сводке группа считается одним замечанием. Переходы принадлежат полям внутри группы. */
export function paymentRequisiteGroup(
  gaps: readonly PaymentRequisiteGap[], readiness: readonly InvoiceReadinessGap[], editable: boolean,
): StatusItem {
  return {
    key: 'payment-requisites', level: 'gate', text: 'Заполните реквизиты оплаты', blocks: ['pay'], actions: [],
    requisiteRows: gaps.map(gap => {
      const blocks: StatusBlock[] = readiness.some(item => matchesPaymentGap(gap, item)) ? ['parsed', 'pay'] : ['pay'];
      return {
        key: gap.field, level: 'gate', text: gap.label, blocks,
        actions: editable ? [{
          kind: 'goto', place: { place: 'field', key: gap.field },
          label: `К полю «${FIELD_LABEL[gap.field] ?? gap.field}»`, short: 'К полю',
        }] : [],
        why: editable ? undefined : 'Заполняет тот, у кого есть право вводить счета',
      };
    }),
  };
}

/** Остальные отказы сохраняют собственный пункт, пока их причина не названа в перечне. */
export function paymentRefusalStatus(
  payment: PaymentView, requisites: readonly PaymentRequisiteGap[], readiness: readonly InvoiceReadinessGap[],
  { visible, closed }: { visible: boolean; closed: boolean },
): { item: StatusItem | null; discrepancy: boolean } {
  const duplicate = payment.refusalKind === 'duplicate';
  const refusal = duplicate ? payment.moneyRefusal : payment.refusal;
  const kind = duplicate ? payment.moneyRefusalKind : payment.refusalKind;
  if (!visible || payment.paid || !refusal || kind === 'rejected') return { item: null, discrepancy: false };

  const names = (test: (gap: InvoiceReadinessGap) => boolean) =>
    readiness.some(gap => gap.blocks?.includes('payment') && test(gap));
  const named = kind === 'amount' ? requisites.some(gap => gap.field === K.total) || names(gap => gap.kind === 'field' && gap.field === K.total)
    : kind === 'discrepancy' ? names(gap => gap.kind === 'total')
    : kind === 'requisites' && requisites.length > 0;
  if (named) return { item: null, discrepancy: false };

  return {
    discrepancy: kind === 'discrepancy',
    item: {
      key: 'pay-refusal', level: 'gate', text: `Оплатить нельзя: ${refusal}`, blocks: ['pay'],
      actions: kind === 'amount' && !closed
        ? [{ kind: 'goto', label: 'К полю', place: { place: 'field', key: K.total } }] : [],
    },
  };
}
