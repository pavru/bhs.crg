import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';

export interface EngineDto {
  enabled: boolean;
  hasKey: boolean;
  model?: string | null;
  baseUrl?: string | null;
  folderId?: string | null;
  host?: string | null;
}

export interface IntegrationSettingsDto {
  recognitionOrder: string[];
  recognition: Record<string, EngineDto>;
  webSearch: Record<string, EngineDto>;
  fgisDomains: string[];
  manufacturerDomains: string[];
}

export interface EngineUpdate {
  enabled: boolean;
  apiKey?: string;            // пусто = оставить прежний
  model?: string | null;
  baseUrl?: string | null;
  folderId?: string | null;
  host?: string | null;
}

export interface IntegrationSettingsUpdate {
  recognitionOrder: string[];
  recognition: Record<string, EngineUpdate>;
  webSearch: Record<string, EngineUpdate>;
  fgisDomains: string[];
  manufacturerDomains: string[];
}

export function useIntegrationSettings() {
  return useQuery({
    queryKey: ['integration-settings'],
    queryFn: () => apiClient.get<IntegrationSettingsDto>('/settings/integrations').then(r => r.data),
  });
}

export interface IntegrationModelsDto {
  anthropic: string[];
  gemini: string[];
  ollama: string[];   // только реально скачанные модели Ollama
}

export function useIntegrationModels() {
  return useQuery({
    queryKey: ['integration-models'],
    queryFn: () => apiClient.get<IntegrationModelsDto>('/settings/integrations/models').then(r => r.data),
    staleTime: 30_000,
  });
}

export function useSaveIntegrationSettings() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (update: IntegrationSettingsUpdate) =>
      apiClient.put('/settings/integrations', update).then(() => undefined),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['integration-settings'] }),
  });
}
