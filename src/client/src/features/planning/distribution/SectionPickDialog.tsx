import { useState } from 'react';
import {
  distributionRefusal, useWriteLines, type Distribution, type DistributionLine, type DistributionRefusal,
} from '@/shared/api/planningDistribution';
import { Modal, ModalButtons, ModalExit, ModalNote, ModalPrimary } from '@/shared/ui/Modal';
import { Select, SelectItem } from '@/shared/ui/Select';
import { useSnackbar } from '@/shared/ui/Snackbar';
import { formatCount } from '@/shared/format/format';
import { ruPlural } from '@/shared/utils/pluralize';
import { wholeToSection } from './distributionView';
import { RefusalBanner } from './RefusalBanner';

const rowsWord = (n: number) => `${formatCount(n)} ${ruPlural(n, 'строку', 'строки', 'строк')}`;

/**
 * «На раздел…» над выбранными строками (`SelectionBar`, макет П2): каждая строка целиком — частью на выбранный
 * раздел, одной записью. Строки без объёма частей не принимают — окно говорит, сколько их пропущено.
 */
export function SectionPickDialog({ distribution, lines, onClose, onDone }: {
  distribution: Distribution;
  lines: DistributionLine[];
  onClose: () => void;
  onDone: () => void;
}) {
  const write = useWriteLines(distribution.revisionId);
  const toast = useSnackbar();
  const [sectionId, setSectionId] = useState('');
  const [refusal, setRefusal] = useState<DistributionRefusal | null>(null);
  const sections = distribution.targets.sections.filter(s => !s.deleted);
  const { writes, skipped } = wholeToSection(lines, sectionId || '-');

  const submit = () => {
    setRefusal(null);
    write.mutate({ body: writes, version: distribution.version }, {
      onSuccess: () => {
        const name = sections.find(s => s.sectionId === sectionId)?.name ?? '';
        toast.success(`${rowsWord(writes.length)} — на раздел «${name}»`);
        onDone();
      },
      onError: e => setRefusal(distributionRefusal(e)),
    });
  };

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title={`На раздел: ${rowsWord(lines.length)}`} busy={write.isPending}>
      <div className="space-y-4">
        <RefusalBanner refusal={refusal} revisionId={distribution.revisionId} onClear={() => setRefusal(null)} />
        <Select label="Раздел стройки" value={sectionId} onValueChange={setSectionId} placeholder="Выберите раздел">
          {sections.map(s => <SelectItem key={s.sectionId} value={s.sectionId}>{s.name}</SelectItem>)}
        </Select>
        <p className="text-body-medium text-on-surface-variant">
          Строка целиком уходит в раздел — её прежние части заменятся.
        </p>
        <ModalButtons>
          {skipped > 0 && <ModalNote>Без объёма — {rowsWord(skipped)}: их раздел задаёт правило</ModalNote>}
          <ModalExit />
          <ModalPrimary disabled={sectionId === '' || writes.length === 0} loading={write.isPending} onClick={submit}>
            Записать {writes.length > 0 ? rowsWord(writes.length) : ''}
          </ModalPrimary>
        </ModalButtons>
      </div>
    </Modal>
  );
}
