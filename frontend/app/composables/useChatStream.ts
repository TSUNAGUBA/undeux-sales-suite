/**
 * AIチャットの SSE ストリーミング受信コンポーザブル。
 *
 * チャット API（POST /api/chat/business, /api/chat/negotiation）は Authorization ヘッダが
 * 必須のため EventSource は使えず、fetch + ReadableStream で text/event-stream を逐次パースする。
 * 事前エラー（400/403/503 等）は application/json の ApiError で返るため、
 * response.ok / Content-Type で分岐して onError ハンドラへ流す（utils/apiError.ts を再利用）。
 */
import type {
  ApiError,
  ChatDoneEvent,
  ChatMessagePayload,
  ChatRole,
  ChatSource,
} from '~/types/api'

/** SSE イベントごとの受信ハンドラ。 */
export interface ChatStreamHandlers {
  /** `sources` イベント（delta 開始前に1回。参照ナレッジ0件時は空配列）。 */
  onSources?: (sources: ChatSource[]) => void
  /** `delta` イベント（応答テキスト断片）。 */
  onDelta?: (text: string) => void
  /** `done` イベント（トークン使用量）。 */
  onDone?: (usage: ChatDoneEvent) => void
  /** `error` イベント・事前 JSON エラー・ネットワークエラー（中断は除く）。 */
  onError?: (error: ApiError) => void
}

/** sendChat の戻り値。abort() で受信を中断できる。 */
export interface ChatStreamHandle {
  /** ストリーム受信を中断する（onError は呼ばれない）。 */
  abort: () => void
  /** ストリーム終了（正常・エラー・中断いずれも）まで待つ Promise。reject しない。 */
  done: Promise<void>
}

/** SSE の1イベント（event 行 + data 行）。 */
interface SseEvent {
  event: string
  data: string
}

/**
 * SSE バッファから完成済みイベント（空行区切り）を取り出す。
 * 戻り値は [パース済みイベント, 未完の残りバッファ]。マルチバイト分断は TextDecoder(stream)
 * 側で解決済みの前提で、ここでは行単位の分断のみ扱う。
 */
function drainSseBuffer(buffer: string, flush: boolean): [SseEvent[], string] {
  const blocks = buffer.split(/\r?\n\r?\n/)
  // 最後の要素は未完のイベントの可能性があるため、flush 時以外はバッファへ戻す。
  const rest = flush ? '' : (blocks.pop() ?? '')
  if (flush && blocks[blocks.length - 1] === '') {
    blocks.pop()
  }
  const events: SseEvent[] = []
  for (const block of blocks) {
    if (block.trim() === '') continue
    let event = 'message'
    const dataLines: string[] = []
    for (const line of block.split(/\r?\n/)) {
      if (line.startsWith('event:')) {
        event = line.slice('event:'.length).trim()
      } else if (line.startsWith('data:')) {
        // SSE 仕様: "data: " の先頭スペース1つは値に含めない。複数 data 行は改行で連結する。
        dataLines.push(line.slice('data:'.length).replace(/^ /, ''))
      }
      // コメント行（: で開始）やその他フィールドは無視する。
    }
    events.push({ event, data: dataLines.join('\n') })
  }
  return [events, rest]
}

/** data の JSON を安全にパースする（壊れた断片はスキップして継続する）。 */
function parseJson<T>(data: string): T | null {
  try {
    return JSON.parse(data) as T
  } catch {
    return null
  }
}

export function useChatStream() {
  const config = useRuntimeConfig()
  const baseURL = config.public.apiBaseUrl
  const { getIdToken } = useAuth()

  /**
   * チャット API へ POST し、SSE ストリームをハンドラへ流す。
   * 戻り値の abort() で中断できる（中断時に onError は呼ばれない）。
   */
  function sendChat(
    path: string,
    body: Record<string, unknown>,
    handlers: ChatStreamHandlers,
  ): ChatStreamHandle {
    const controller = new AbortController()

    function dispatch(events: SseEvent[]): boolean {
      for (const { event, data } of events) {
        if (event === 'sources') {
          const payload = parseJson<{ sources: ChatSource[] }>(data)
          if (payload) handlers.onSources?.(payload.sources ?? [])
        } else if (event === 'delta') {
          const payload = parseJson<{ text: string }>(data)
          if (payload?.text) handlers.onDelta?.(payload.text)
        } else if (event === 'done') {
          const payload = parseJson<ChatDoneEvent>(data)
          if (payload) handlers.onDone?.(payload)
        } else if (event === 'error') {
          // error イベント後にストリームは終了する（契約）。以降の読み取りは打ち切る。
          const payload = parseJson<ApiError>(data)
          handlers.onError?.(
            payload ?? { errorCode: 'UNKNOWN', summary: 'AI 応答の受信中にエラーが発生しました。', remedy: '時間をおいて再送してください。' },
          )
          return true
        }
      }
      return false
    }

    async function run(): Promise<void> {
      try {
        const token = await getIdToken()
        const response = await fetch(`${baseURL}${path}`, {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            Accept: 'text/event-stream',
            ...(token ? { Authorization: `Bearer ${token}` } : {}),
          },
          body: JSON.stringify(body),
          signal: controller.signal,
        })

        const contentType = response.headers.get('content-type') ?? ''
        if (!response.ok || !contentType.includes('text/event-stream')) {
          // 事前エラー（AI 未設定 503・検証 400 等）は ApiError の JSON で返る。
          const data: unknown = await response.json().catch(() => null)
          const apiError =
            extractApiError({ data }) ??
            {
              errorCode: 'UNKNOWN',
              summary: apiErrorMessage({ status: response.status, data }),
              remedy: '',
            }
          handlers.onError?.(apiError)
          return
        }

        if (!response.body) {
          handlers.onError?.({
            errorCode: 'UNKNOWN',
            summary: '応答ストリームを取得できませんでした。',
            remedy: 'ブラウザを更新して再度お試しください。',
          })
          return
        }

        // マルチバイト（UTF-8）がチャンク境界で分断されても壊れないよう stream:true で復号する。
        const reader = response.body.getReader()
        const decoder = new TextDecoder('utf-8')
        let buffer = ''
        try {
          for (;;) {
            const { done: finished, value } = await reader.read()
            if (finished) break
            buffer += decoder.decode(value, { stream: true })
            const [events, rest] = drainSseBuffer(buffer, false)
            buffer = rest
            if (dispatch(events)) {
              await reader.cancel().catch(() => undefined)
              return
            }
          }
          buffer += decoder.decode()
          const [events] = drainSseBuffer(buffer, true)
          dispatch(events)
        } finally {
          reader.releaseLock()
        }
      } catch (error) {
        // 呼び出し側の abort() による中断はエラー扱いにしない。
        if (controller.signal.aborted) return
        handlers.onError?.(
          extractApiError(error) ?? {
            errorCode: 'UNKNOWN',
            summary: apiErrorMessage(error),
            remedy: '通信環境を確認のうえ、再度お試しください。',
          },
        )
      }
    }

    return {
      abort: () => controller.abort(),
      done: run(),
    }
  }

  return { sendChat }
}

// ============================================================
// 会話状態（メッセージ配列 + ストリーミング進行）の共通オーケストレーション
// 業務チャット・商談チャットの2画面で送信フローが同一のため共通化する（原則3: 既存パターンの再利用）。
// ============================================================

/** チャット UI 上の1メッセージ（API の ChatMessagePayload + 表示用メタ）。 */
export interface ChatUiMessage {
  role: ChatRole
  content: string
  /** アシスタント応答が参照したナレッジ（sources イベント由来）。 */
  sources?: ChatSource[]
  /** エラー通知バブルとして描画するか（API へ送る履歴からは除外する）。 */
  isError?: boolean
  /** UI 上の通知（停止案内等）。通常のバブルで描画するが API へ送る履歴からは除外する。 */
  isNotice?: boolean
}

/** 会話1本ぶんの状態と送信・停止・クリア操作。 */
export function useChatConversation() {
  const { sendChat } = useChatStream()

  const messages = ref<ChatUiMessage[]>([])
  const streaming = ref(false)
  let handle: ChatStreamHandle | null = null
  let stoppedByUser = false

  /** API へ送る会話履歴（エラー・通知バブルと空メッセージは除外。古い順）。 */
  function payloadMessages(): ChatMessagePayload[] {
    return messages.value
      .filter((m) => !m.isError && !m.isNotice && m.content !== '')
      .map((m) => ({ role: m.role, content: m.content }))
  }

  /** アシスタント応答をエラーバブル化する（部分応答がある場合は別バブルで追加する）。 */
  function applyError(assistant: ChatUiMessage, error: ApiError): void {
    const text = error.remedy
      ? `[${error.errorCode}] ${error.summary}\n${error.remedy}`
      : `[${error.errorCode}] ${error.summary}`
    if (assistant.content === '') {
      assistant.content = text
      assistant.isError = true
      assistant.sources = undefined
    } else {
      messages.value.push({ role: 'assistant', content: text, isError: true })
    }
  }

  /**
   * ユーザー発話を追加してチャット API へ送信し、アシスタント応答をストリーム反映する。
   * @param path チャットエンドポイント（/api/chat/business 等）
   * @param extra messages 以外のリクエストフィールド（domain / businessTypeCode 等）
   */
  function send(text: string, path: string, extra: Record<string, unknown>): void {
    if (streaming.value) return
    const content = text.trim()
    if (content === '') return

    messages.value.push({ role: 'user', content })
    const payload = payloadMessages()
    messages.value.push({ role: 'assistant', content: '', sources: [] })
    // push 後に配列から取り直すことで、リアクティブプロキシ経由の参照を保持する。
    const assistant = messages.value[messages.value.length - 1]!

    streaming.value = true
    stoppedByUser = false
    handle = sendChat(path, { ...extra, messages: payload }, {
      onSources: (sources) => {
        assistant.sources = sources
      },
      onDelta: (delta) => {
        assistant.content += delta
      },
      onError: (error) => {
        applyError(assistant, error)
      },
    })
    handle.done.finally(() => {
      streaming.value = false
      // 応答が1文字も届かず終了した場合の空バブル対策（停止時は通知、それ以外はエラー扱い）。
      if (!assistant.isError && assistant.content === '') {
        if (stoppedByUser) {
          assistant.content = '（送信を停止しました）'
          assistant.isNotice = true
        } else {
          assistant.content = '応答を受信できませんでした。時間をおいて再送してください。'
          assistant.isError = true
        }
        assistant.sources = undefined
      }
    })
  }

  /** ストリーミング受信を停止する（受信済みの部分応答は残す）。 */
  function stop(): void {
    stoppedByUser = true
    handle?.abort()
  }

  /** 会話をクリアする（受信中なら中断してから空にする）。 */
  function clear(): void {
    handle?.abort()
    streaming.value = false
    messages.value = []
  }

  // ページ離脱時に受信中のストリームを確実に中断する（バックグラウンドの取りこぼし防止）。
  onScopeDispose(() => {
    handle?.abort()
  })

  return { messages, streaming, send, stop, clear }
}
