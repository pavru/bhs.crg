import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ThemeProvider } from '@/shared/ui/ThemeProvider';
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
import { UsersPage } from '@/features/settings/UsersPage';
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
        <BrowserRouter>
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
                <Route path="profile" element={<ProfilePage />} />
                <Route element={<AdminRoute />}>
                  <Route path="document-types/*" element={<DocumentTypesPage kind="Document" />} />
                  <Route path="composite-types/*" element={<DocumentTypesPage kind="Composite" />} />
                  <Route path="templates/*" element={<TemplatesPage />} />
                  <Route path="field-types" element={<PrimitiveTypesPage />} />
                  <Route path="users" element={<UsersPage />} />
                  <Route path="settings" element={<SettingsPage />} />
                </Route>
              </Route>
            </Route>
          </Routes>
        </BrowserRouter>
      </AuthProvider>
    </QueryClientProvider>
    </ThemeProvider>
  );
}
