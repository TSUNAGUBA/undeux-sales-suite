import type { ApiError } from '~/types/api'

/** $fetch のエラーからAPIのエラーレスポンス（ApiError）を抽出する。 */
export function extractApiError(error: unknown): ApiError | null {
  const data = (error as { data?: unknown } | null)?.data
  if (data && typeof data === 'object' && 'errorCode' in data) {
    return data as ApiError
  }
  return null
}

/** $fetch（ofetch）のエラーからHTTPステータスコードを取り出す。取得できなければ null。 */
function httpStatusOf(error: unknown): number | null {
  if (error && typeof error === 'object') {
    const e = error as { statusCode?: number; status?: number; response?: { status?: number } }
    return e.statusCode ?? e.status ?? e.response?.status ?? null
  }
  return null
}

/** エラーからユーザー向け表示メッセージを生成する。 */
export function apiErrorMessage(error: unknown): string {
  const apiError = extractApiError(error)
  if (apiError) {
    // detail は「同一コードを複数経路で共用する場合に、どの経路かを補う」ための欄
    // （UNDX-REQ-008 / REQ-009 等）。サーバは summary と異なるときだけ詰めるため
    // （ExceptionHandlingMiddleware）、そのまま連結しても重複しない。
    // ここで落とすと、汎用化した Summary だけが残って案内が後退する。
    return apiError.detail
      ? `[${apiError.errorCode}] ${apiError.summary} ${apiError.detail}`
      : `[${apiError.errorCode}] ${apiError.summary}`
  }
  // ApiError 本文を伴わない認証・認可エラー（本文空の 401/403）は専用の案内にする。
  const status = httpStatusOf(error)
  if (status === 401) {
    return 'ログインが必要です。再度サインインしてください。'
  }
  if (status === 403) {
    return 'この操作を行う権限がありません。'
  }
  if (error instanceof Error && error.message) {
    return error.message
  }
  return '予期しないエラーが発生しました。'
}
