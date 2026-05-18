import type { ApiError } from '~/types/api'

/** $fetch のエラーからAPIのエラーレスポンス（ApiError）を抽出する。 */
export function extractApiError(error: unknown): ApiError | null {
  const data = (error as { data?: unknown } | null)?.data
  if (data && typeof data === 'object' && 'errorCode' in data) {
    return data as ApiError
  }
  return null
}

/** エラーからユーザー向け表示メッセージを生成する。 */
export function apiErrorMessage(error: unknown): string {
  const apiError = extractApiError(error)
  if (apiError) {
    return `[${apiError.errorCode}] ${apiError.summary}`
  }
  if (error instanceof Error && error.message) {
    return error.message
  }
  return '予期しないエラーが発生しました。'
}
