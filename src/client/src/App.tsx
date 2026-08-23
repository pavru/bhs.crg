import { BrowserRouter, Routes, Route, Navigate } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ThemeProvider } from '@/shared/ui/ThemeProvider';
import { ErrorBoundary } from '@/shared/ui/ErrorBoundary';
import { NavigationGuardProvider } from '@/shared/ui/NavigationGuard';
import { DocumentTitleManager } from '@/shared/ui/DocumentTitle';
import { ToastProvider } from '@/shared/ui/Toast';
import { BugReportProvider } from '@/shared/ui/BugReportProvider';
import { AuthProvider } from '@/shared/ui/AuthProvider';
import { ProtectedRoute, AdminRoute } from '@/shared/ui/ProtectedRoute';
import { AppShell } from '@/shared/ui/AppShell';
import { LoginPage } from '@/features/catalog/LoginPage';
import { DocumentTypesPage } from '@/features/settings/DocumentTypesPage';
import { TemplatesPage } from '@/features/templates/TemplatesPage';
import { DocumentSetsPage } from '@/features/document-sets/DocumentSetsPage';
import { SystemCommonDataPage } from '@/features/common-data/SystemCommonDataPage';
import { SettingsPage } from '@/features/settings/SettingsPage';
import { PrimitiveTypesPage } from '@/features/settings/PrimitiveTypesPage';
import { RecognitionProfilesPage } from '@/features/settings/RecognitionProfilesPage';
import { ReconciliationsPage } from '@/features/reconciliations/ReconciliationsPage';
import { UsersPage } from '@/features/settings/UsersPage';
import { BugReportsPage } from '@/features/settings/BugReportsPage';
import { DataSetsPage } from '@/features/datasets/DataSetsPage';
import { PdfGroupingEditor } from '@/features/datasets/PdfGroupingEditor';
import { QualityDocsPage } from '@/features/quality-docs/QualityDocsPage';
import { ProfilePage } from '@/features/account/ProfilePage';
import { ForgotPasswordPage } from '@/features/auth/ForgotPasswordPage';
import { ResetPasswordPage } from '@/features/auth/ResetPasswordPage';
import { ConfirmEmailPage } from '@/features/auth/ConfirmEmailPage';
import { ConfirmEmailChangePage } from '@/features/auth/ConfirmEmailChangePage';

const queryClient = new QueryClient({
  defaultOptions: { queries: { staleTime: 30_000, retry: 1 } },
});

export default function App() {
  return (
    <ThemeProvider>
    <QueryClientProvider client={queryClient}>
      <AuthProvider>
        <ToastProvider>
        {/* Выше корневой границы ошибок намеренно (issue #834): поймав сбой, граница РАЗМОНТИРУЕТ
            своих детей — окажись форма внутри, кнопка «Сообщить об ошибке» пропала бы ровно на том
            экране, ради которого она и заведена. */}
        <BugReportProvider>
        <BrowserRouter>
          <ErrorBoundary variant="page" allowReload>
          <NavigationGuardProvider>
          <DocumentTitleManager>
          <Routes>
            <Route path="/login" element={<LoginPage />} />
            <Route path="/forgot-password" element={<ForgotPasswordPage />} />
            <Route path="/reset-password" element={<ResetPasswordPage />} />
            <Route path="/confirm-email" element={<ConfirmEmailPage />} />
            <Route path="/confirm-email-change" element={<ConfirmEmailChangePage />} />
            <Route element={<ProtectedRoute />}>
              <Route element={<AppShell />}>
                <Route index element={<Navigate to="/document-sets" replace />} />
                <Route path="document-sets/*" element={<DocumentSetsPage />} />
                <Route path="common-data" element={<SystemCommonDataPage />} />
                <Route path="datasets" element={<DataSetsPage />} />
                <Route path="datasets/files/:fileId/grouping" element={<PdfGroupingEditor />} />
                <Route path="quality-docs" element={<QualityDocsPage />} />
                <Route path="reconciliations" element={<ReconciliationsPage />} />
                <Route path="profile" element={<ProfilePage />} />
                <Route element={<AdminRoute />}>
                  {/* key — чтобы React перемонтировал страницу между двумя маршрутами: без него это
                      один экземпляр компонента (тот же тип в той же позиции дерева), и поиск,
                      раскрытые группы и восстановленный выбор переезжали с одной страницы на
                      другую (issue #782). */}
                  <Route path="document-types/*" element={<DocumentTypesPage key="document" kind="Document" />} />
                  <Route path="composite-types/*" element={<DocumentTypesPage key="composite" kind="Composite" />} />
                  <Route path="templates/*" element={<TemplatesPage />} />
                  <Route path="field-types" element={<PrimitiveTypesPage />} />
                  <Route path="recognition-profiles" element={<RecognitionProfilesPage />} />
                  <Route path="users" element={<UsersPage />} />
                  <Route path="bug-reports" element={<BugReportsPage />} />
                  <Route path="settings" element={<SettingsPage />} />
                </Route>
              </Route>
            </Route>
          </Routes>
          </DocumentTitleManager>
          </NavigationGuardProvider>
          </ErrorBoundary>
        </BrowserRouter>
        </BugReportProvider>
        </ToastProvider>
      </AuthProvider>
    </QueryClientProvider>
    </ThemeProvider>
  );
}
