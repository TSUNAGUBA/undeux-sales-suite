import type { MartStatus } from '~/types/api'

/**
 * 分析 mart（スタースキーマ）の構築状態・再構築を扱う共有コンポーザブル。
 *
 * 状態は {@link useState} で全 mart ページ間に共有する。各ページは {@link refreshStatus} で
 * 構築状態を取得し、{@link isBuilt} が false のときは「未構築」ガードを表示する。再構築
 * （{@link rebuild}）は全社サマリーページから実行する想定で、バックグラウンド実行を
 * `/api/mart/status` のポーリングで完了まで追跡する（同期実行のタイムアウトを避ける）。
 */
export function useMart() {
  const status = useState<MartStatus | null>('mart-status', () => null)
  const rebuilding = useState<boolean>('mart-rebuilding', () => false)
  const rebuildMessage = useState<string | null>('mart-rebuild-message', () => null)
  const { get, post } = useApi()

  const isBuilt = computed(
    () => status.value?.built === true && (status.value?.factRows ?? 0) > 0,
  )

  /** mart 構築状態を取得して共有 state を更新する。失敗時は例外を呼び出し側へ伝播する。 */
  async function refreshStatus(): Promise<MartStatus> {
    const result = await get<MartStatus>('/api/mart/status')
    status.value = result
    return result
  }

  function sleep(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms))
  }

  /**
   * mart 再構築を開始し、完了までポーリングする。
   * @param onCompleted 再構築が completed になった直後に呼ばれるコールバック（再読込など）。
   * @returns エラーメッセージ（成功時は null）。
   */
  async function rebuild(onCompleted?: () => Promise<void> | void): Promise<string | null> {
    rebuilding.value = true
    rebuildMessage.value = null
    try {
      // POST は即座に running を返す。完了まで status を 3 秒間隔でポーリングする。
      status.value = await post<MartStatus>('/api/mart/rebuild')
      for (let i = 0; i < 300 && status.value?.status === 'running'; i++) {
        await sleep(3000)
        status.value = await get<MartStatus>('/api/mart/status')
      }
      if (status.value?.status === 'completed') {
        rebuildMessage.value = 'mart を再構築しました。'
        await onCompleted?.()
        return null
      }
      if (status.value?.status === 'failed') {
        return `再構築に失敗しました: ${status.value.error ?? '原因不明'}`
      }
      if (status.value?.status === 'running') {
        rebuildMessage.value =
          '再構築は実行中です。完了までしばらくお待ちください（このまま待つか、後でページを再読み込みしてください）。'
      }
      return null
    } catch (error) {
      return apiErrorMessage(error)
    } finally {
      rebuilding.value = false
    }
  }

  return { status, isBuilt, rebuilding, rebuildMessage, refreshStatus, rebuild }
}
