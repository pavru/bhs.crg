import axios from 'axios';
import { getToken, clearToken } from './token';

export const apiClient = axios.create({
  baseURL: import.meta.env.VITE_API_URL ?? '/api',
});

apiClient.interceptors.request.use((config) => {
  const token = getToken();
  if (token) config.headers.Authorization = `Bearer ${token}`;
  return config;
});

apiClient.interceptors.response.use(
  (r) => r,
  (err) => {
    if (err.response?.status === 401) {
      clearToken();
      window.location.href = '/login';
    }
    const serverMessage = err.response?.data?.error ?? err.response?.data?.detail;
    if (serverMessage) {
      err.message = serverMessage;
    }
    return Promise.reject(err);
  }
);
