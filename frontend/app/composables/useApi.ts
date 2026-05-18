type RequestBody = Record<string, unknown> | FormData

interface ApiRequestOptions {
  method?: 'GET' | 'POST'
  query?: Record<string, unknown>
  body?: RequestBody
}

/**
 * バックエンドAPIへのリクエストを行うコンポーザブル。
 * Firebase IDトークンを Authorization ヘッダに自動付与する。
 */
export function useApi() {
  const config = useRuntimeConfig()
  const baseURL = config.public.apiBaseUrl
  const { getIdToken } = useAuth()

  async function request<T>(path: string, options: ApiRequestOptions): Promise<T> {
    const token = await getIdToken()
    return await $fetch<T>(path, {
      baseURL,
      method: options.method ?? 'GET',
      query: options.query,
      body: options.body,
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    })
  }

  return {
    get: <T>(path: string, query?: Record<string, unknown>): Promise<T> =>
      request<T>(path, { method: 'GET', query }),
    post: <T>(path: string, body?: RequestBody): Promise<T> =>
      request<T>(path, { method: 'POST', body }),
  }
}
