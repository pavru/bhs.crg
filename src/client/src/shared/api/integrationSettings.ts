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

export interface SmtpDto {
  enabled: boolean;
  host?: string | null;
  port: number;
  user?: string | null;
  hasPassword: boolean;
  from?: string | null;
  fromName?: string | null;
  useSsl: boolean;
}

export interface IntegrationSettingsDto {
  recognitionOrder: string[];
  recognition: Record<string, EngineDto>;
  webSearch: Record<string, EngineDto>;
  fgisDomains: string[];
  manufacturerDomains: string[];
  smtp: SmtpDto;
}

/** Обновление SMTP (пустой password = оставить прежний). */
export interface SmtpUpdate {
  enabled: boolean;
  host?: string | null;
  port: number;
  user?: string | null;
  password?: string;
  from?: string | null;
  fromName?: string | null;
  useSsl: boolean;
}

export interface UserEmailStatus {
  displayName: string;
  email: string | null;
  valid: boolean;
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

/** Сохранение только SMTP-секции (не затирает распознавание/поиск). */
export function useSaveSmtp() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (update: SmtpUpdate) =>
      apiClient.put('/settings/integrations/email', update).then(() => undefined),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['integration-settings'] }),
  });
}

/** Тест-отправка на указанный адрес. Возвращает {ok} или {ok:false, error}. */
export function useTestEmail() {
  return useMutation({
    mutationFn: (to: string) =>
      apiClient.post<{ ok: boolean; error?: string }>('/settings/integrations/email/test', { to }).then(r => r.data),
  });
}

/** Статус email пользователей (у кого задан/валиден адрес). */
export function useEmailUserStatus() {
  return useQuery({
    queryKey: ['email-user-status'],
    queryFn: () => apiClient.get<UserEmailStatus[]>('/settings/integrations/email/user-status').then(r => r.data),
  });
}
