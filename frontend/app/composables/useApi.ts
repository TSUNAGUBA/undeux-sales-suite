// 送信ボディ。JSON 化するオブジェクト（インラインリテラルと型付きインターフェースの両方）または
// multipart の FormData。型付きインターフェースは Record<string, unknown> の添字シグネチャを満たさないため、
// オブジェクト全般を受ける object を含める（$fetch が JSON 直列化する）。
type RequestBody = Record<string, unknown> | object | FormData

interface ApiRequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'DELETE'
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
    put: <T>(path: string, body?: RequestBody): Promise<T> =>
      request<T>(path, { method: 'PUT', body }),
    del: <T = void>(path: string): Promise<T> =>
      request<T>(path, { method: 'DELETE' }),
  }
}
