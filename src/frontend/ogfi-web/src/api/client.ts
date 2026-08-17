import createClient from 'openapi-fetch'
import type { paths } from './schema'

const defaultApiBaseUrl = typeof window !== 'undefined' ? window.location.origin : 'http://localhost'

export const apiClient = createClient<paths>({
  baseUrl: import.meta.env.VITE_API_BASE_URL || defaultApiBaseUrl,
  credentials: 'include',
})
