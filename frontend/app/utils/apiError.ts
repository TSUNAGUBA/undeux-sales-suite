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
    // （UNDX-REQ-008 / REQ-009 等）。ここで落とすと、汎用化した Summary だけが残り案内が後退する。
    // summary と重複するときは連結しない。現状のサーバ実装では detail が summary と
    // 完全一致する経路は無い（AppException 経路は ExceptionHandlingMiddleware が
    // 一致時に null 化し、他の生成箇所も別文言）。将来同じ文が返ってきた場合の保険であり、
    // 現時点では到達しない防御である。
    const detail = apiError.detail && apiError.detail !== apiError.summary ? apiError.detail : null
    return detail
      ? `[${apiError.errorCode}] ${apiError.summary} ${detail}`
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
