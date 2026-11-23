import type { HiddenPart, PlanMark } from './planningEstimates';

export type OfferStatus = 'draft' | 'sent' | OfferOutcome;
export type OfferOutcome = 'accepted' | 'rejected' | 'no-answer';
export type OfferVatMode = 'none' | 'included' | 'on-top';
export type OfferSource = 'estimate' | 'pricelist';
export type MarkupLevel = 'offer' | 'section' | 'line';

export interface OfferRevisionRef { id: string; number: number }
export interface OfferRevision extends OfferRevisionRef { estimateId: string; estimateName: string }
export interface OfferDrift {
  code: 'none' | 'newer-revision' | 'current-changed';
  text: string;
  source: OfferRevisionRef;
  current: OfferRevisionRef | null;
  latest: OfferRevisionRef;
}

export interface OfferCustomer { id: string; name: string | null; archived: boolean; lost: boolean }
export interface OfferVat { mode: OfferVatMode; rate: number | null }
export interface OfferMoney { linesTotal: number; vat: number | null; total: number; incomplete: boolean }
export interface OfferCost { total: number | null; margin: number | null }
export interface OfferSection { code: string; name: string | null; lines: number; markup: number | null }
export interface OfferLineSection { code: string | null; name: string | null }
export interface OfferPriceSource { kind: 'pricelist' | 'none'; validFrom?: string }

export interface OfferLine {
  id: string;
  order: number;
  number: string | null;
  lsrSection: OfferLineSection | null;
  name: string;
  volume: number | null;
  unit: string | null;
  workTypeId: string | null;
  kind: 'work' | 'material';
  ownMarkup: number | null;
  markup: { value: number; level: MarkupLevel };
  price: number | null;
  amount: number | null;
  marks: PlanMark[];
  cost?: OfferCost;
  priceSource?: OfferPriceSource;
  priceListUnitId?: string;
}

export interface OfferListItem {
  id: string;
  number: string;
  date: string;
  status: OfferStatus;
  currentVersion: number;
  sentOn: string | null;
  version: string;
  constructionId: string;
  customer: OfferCustomer | null;
  revision: OfferRevision | null;
  vat: OfferVat;
  money: OfferMoney;
  changedAt: string;
  source: OfferSource;
  drift?: OfferDrift | null;
}

export interface Offer extends OfferListItem {
  markup: number;
  sections: OfferSection[];
  lines: OfferLine[];
  data: Record<string, unknown>;
  createdAt: string;
  createdBy: string | null;
  cost?: OfferCost;
  hidden?: HiddenPart[];
  priceListDate: string | null;
  canBuildSchedule: boolean;
  scheduleReason: string | null;
}

export interface OfferHeaderInput {
  date: string;
  customerId: string | null;
  vatMode: OfferVatMode;
  vatRate: number | null;
  data: Record<string, unknown>;
}

export interface CreateOfferInput extends Partial<OfferHeaderInput> { revisionId: string; markup?: number }
export interface OfferMarkupsInput {
  markup: number;
  sections: { code: string; markup: number }[];
  lines: { lineId: string; markup: number }[];
}

export interface OfferSnapshotLine {
  id: string;
  number: string | null;
  name: string;
  lsrSectionCode: string | null;
  lsrSectionName: string | null;
  volume: number | null;
  unit: string | null;
  markup: number | null;
  price: number | null;
  amount: number | null;
  priceSource: OfferPriceSource | null;
}

export interface OfferVersion {
  number: number;
  status: OfferStatus;
  sentOn: string | null;
  total: number;
  at: string;
  authorId: string | null;
  content: {
    date: string;
    revisionId: string | null;
    source: OfferSource;
    vat: OfferVat;
    money: OfferMoney;
    markup: number;
    sections: { sectionCode: string; markup: number }[];
    lines: OfferSnapshotLine[];
    authorName?: string;
  };
}

export interface OfferRebaseLine {
  revisionRowId: string;
  lineId: string | null;
  number: string | null;
  lsrSection: OfferLineSection | null;
  name: string;
  volume: number | null;
  unit: string | null;
  workTypeId: string | null;
  kind: 'work' | 'material';
  ownMarkup: number | null;
  price: number | null;
  amount: number;
  cost?: OfferCost;
}

export interface OfferRebasePreview {
  version: string;
  source: OfferRevisionRef;
  target: OfferRevisionRef;
  fingerprint: string;
  added: OfferRebaseLine[];
  removed: OfferRebaseLine[];
  changed: { before: OfferRebaseLine; after: OfferRebaseLine }[];
  unmatched: { before: OfferRebaseLine[]; after: OfferRebaseLine[] }[];
  unmatchedSectionMarkups: { source: OfferLineSection; markup: number; candidates: OfferLineSection[] }[];
  hidden?: HiddenPart[];
}
