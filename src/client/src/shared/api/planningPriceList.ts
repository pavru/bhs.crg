import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';

const PATH = '/plan/pricelist';
const KEY = ['planning', 'pricelist'] as const;

export interface PriceListEntry {
  id: string;
  workTypeId: string;
  unitId: string;
  unit: string;
  validFrom: string;
  price: number;
  version: string;
}

export interface PriceListWork {
  workTypeId: string;
  code: string | null;
  name: string | null;
  unit: string | null;
  unitId: string | null;
  archived: boolean;
  effective: PriceListEntry | null;
  history: PriceListEntry[];
}

interface PriceList {
  date: string;
  items: PriceListWork[];
}

export function readPriceList(date?: string, signal?: AbortSignal) {
  return apiClient.get<PriceList>(PATH, { params: { date }, signal }).then(r => r.data);
}

export function usePriceList(date?: string, enabled = true) {
  return useQuery({ queryKey: [...KEY, date], queryFn: ({ signal }) => readPriceList(date, signal), enabled, retry: false });
}

type PriceWrite =
  | { type: 'save'; workTypeId: string; validFrom: string; price: number; version?: string }
  | { type: 'remove'; entry: PriceListEntry };

export function useWritePrice() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (command: PriceWrite) => {
      if (command.type === 'remove') {
        await apiClient.delete(`${PATH}/entries/${command.entry.id}`, { headers: { 'If-Match': command.entry.version } });
        return;
      }
      const { workTypeId, validFrom, price, version } = command;
      await apiClient.put(`${PATH}/${workTypeId}`, { validFrom, price },
        version === undefined ? undefined : { headers: { 'If-Match': version } });
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: KEY }),
  });
}
