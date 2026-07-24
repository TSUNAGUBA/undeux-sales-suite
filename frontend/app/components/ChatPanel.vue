<script setup lang="ts">
/**
 * AIチャット共通パネル（業務チャット・商談チャットで共有）。
 *
 * - メッセージ領域はパネル内スクロール（親から flex-1 / min-h-0 で高さを与える前提）。
 * - 新着で自動的に最下部へ追従する（ユーザーが上へスクロール中は追従を止める）。
 * - コンポーザは Enter=送信 / Shift+Enter=改行（タッチ端末では Enter=改行）。IME 変換確定の
 *   Enter（isComposing）では送信しない。
 */
import { BookOpenText, ChevronDown, Eraser, FileText, Send, Sparkles, Square, TriangleAlert } from 'lucide-vue-next'
import type { ChatUiMessage } from '~/composables/useChatStream'

const props = defineProps<{
  messages: ChatUiMessage[]
  /** アシスタント応答の受信中か（停止ボタン・生成中ドットの表示）。 */
  streaming: boolean
  /** 入力を無効化するか（AI 未設定・選択未完了など）。 */
  disabled?: boolean
  placeholder?: string
  /** メッセージ0件時のヒント文（empty スロットでも差し替え可能）。 */
  emptyHint?: string
}>()

const emit = defineEmits<{ send: [text: string]; stop: []; clear: [] }>()

// ---------------------------------------------------------------
// コンポーザ（自動リサイズ textarea）
// ---------------------------------------------------------------
const draft = ref('')
const composer = ref<HTMLTextAreaElement | null>(null)

/** タッチ主体の端末では Enter を改行に割り当てる（送信は明示ボタン）。 */
const isCoarsePointer =
  typeof window !== 'undefined' && window.matchMedia('(pointer: coarse)').matches

/** textarea を内容に合わせて伸縮させる（最大 約6行 = 144px）。 */
function autoGrow(): void {
  const el = composer.value
  if (!el) return
  el.style.height = 'auto'
  el.style.height = `${Math.min(el.scrollHeight, 144)}px`
}

watch(draft, () => nextTick(autoGrow))

function submit(): void {
  const text = draft.value.trim()
  if (text === '' || props.disabled || props.streaming) return
  emit('send', text)
  draft.value = ''
}

function onEnter(event: KeyboardEvent): void {
  // IME 変換確定の Enter・Shift+Enter・タッチ端末は改行として扱う。
  if (event.isComposing || event.shiftKey || isCoarsePointer) return
  event.preventDefault()
  submit()
}

function onClear(): void {
  if (window.confirm('会話をすべてクリアします。よろしいですか？')) {
    emit('clear')
  }
}

// ---------------------------------------------------------------
// 参照ナレッジ（sources）の開閉状態
// ---------------------------------------------------------------
const openSources = ref(new Set<number>())

function toggleSources(index: number): void {
  if (openSources.value.has(index)) {
    openSources.value.delete(index)
  } else {
    openSources.value.add(index)
  }
}

// ---------------------------------------------------------------
// 自動スクロール（最下部追従）
// ---------------------------------------------------------------
const scrollArea = ref<HTMLElement | null>(null)
let stickToBottom = true

function onScroll(): void {
  const el = scrollArea.value
  if (!el) return
  stickToBottom = el.scrollTop + el.clientHeight >= el.scrollHeight - 48
}

watch(
  () =>
    `${props.messages.length}:${props.messages[props.messages.length - 1]?.content.length ?? 0}`,
  async () => {
    if (props.messages.length === 0) {
      openSources.value.clear()
      stickToBottom = true
      return
    }
    if (!stickToBottom) return
    await nextTick()
    const el = scrollArea.value
    if (el) el.scrollTop = el.scrollHeight
  },
)

/** 生成中ドットを出すか（受信中の最後のアシスタントバブルのみ）。 */
function showTypingDots(index: number): boolean {
  const message = props.messages[index]
  return (
    props.streaming &&
    index === props.messages.length - 1 &&
    message?.role === 'assistant' &&
    !message.isError
  )
}
</script>

<template>
  <div class="flex min-h-0 flex-col overflow-hidden rounded-xl border border-slate-200 bg-slate-50/50 shadow-sm">
    <!-- メッセージ領域（パネル内スクロール） -->
    <div
      ref="scrollArea"
      class="min-h-0 flex-1 space-y-3 overflow-y-auto p-3 sm:p-4"
      @scroll.passive="onScroll"
    >
      <div
        v-if="messages.length === 0"
        class="flex h-full flex-col items-center justify-center gap-2 py-10 text-center"
      >
        <slot name="empty">
          <Sparkles class="h-8 w-8 text-indigo-300" />
          <p class="max-w-md text-sm text-slate-400">{{ emptyHint ?? 'メッセージを入力して会話を開始してください。' }}</p>
        </slot>
      </div>

      <div
        v-for="(message, index) in messages"
        :key="index"
        class="flex"
        :class="message.role === 'user' ? 'justify-end' : 'justify-start'"
      >
        <!-- ユーザー（右・インディゴ） -->
        <div
          v-if="message.role === 'user'"
          class="max-w-[85%] rounded-2xl rounded-br-md bg-indigo-600 px-3.5 py-2.5 shadow-sm sm:max-w-[75%]"
        >
          <p class="whitespace-pre-wrap break-words text-sm leading-relaxed text-white">{{ message.content }}</p>
        </div>

        <!-- アシスタント（左・白カード / エラーは赤系） -->
        <div v-else class="max-w-[85%] sm:max-w-[75%]">
          <div
            class="rounded-2xl rounded-bl-md border px-3.5 py-2.5"
            :class="message.isError
              ? 'border-red-200 bg-red-50'
              : 'border-slate-200 bg-white shadow-sm'"
          >
            <p
              v-if="message.isError"
              class="mb-1 flex items-center gap-1 text-xs font-medium text-red-600"
            >
              <TriangleAlert class="h-3.5 w-3.5 shrink-0" />
              エラー
            </p>
            <p
              class="whitespace-pre-wrap break-words text-sm leading-relaxed"
              :class="message.isError ? 'text-red-700' : 'text-slate-700'"
            >{{ message.content }}</p>
            <span
              v-if="showTypingDots(index)"
              class="mt-1 inline-flex items-center gap-1 py-1"
              role="status"
              aria-label="AI が応答を生成中"
            >
              <span class="h-1.5 w-1.5 animate-bounce rounded-full bg-indigo-400" />
              <span class="h-1.5 w-1.5 animate-bounce rounded-full bg-indigo-400 [animation-delay:150ms]" />
              <span class="h-1.5 w-1.5 animate-bounce rounded-full bg-indigo-400 [animation-delay:300ms]" />
            </span>
          </div>

          <!-- 参照ナレッジ（折りたたみ） -->
          <template v-if="message.sources && message.sources.length > 0">
            <button
              type="button"
              class="mt-1.5 inline-flex items-center gap-1 rounded-full border border-slate-200 bg-white px-2.5 py-1 text-xs text-slate-500 hover:bg-slate-100"
              @click="toggleSources(index)"
            >
              <BookOpenText class="h-3 w-3" />
              参照ナレッジ {{ message.sources.length }}件
              <ChevronDown
                class="h-3 w-3 transition-transform"
                :class="openSources.has(index) ? 'rotate-180' : ''"
              />
            </button>
            <ul v-if="openSources.has(index)" class="mt-1.5 space-y-1">
              <li
                v-for="source in message.sources"
                :key="`${source.entryId}-${source.seq}`"
                class="flex items-center gap-1.5 rounded-lg border border-slate-200 bg-white px-2 py-1 text-xs text-slate-600"
              >
                <FileText class="h-3 w-3 shrink-0 text-slate-400" />
                <span class="min-w-0 truncate" :title="source.title">{{ source.title }}</span>
                <span class="shrink-0 text-slate-400">#{{ source.seq }}</span>
                <span class="shrink-0 rounded border border-slate-200 bg-slate-50 px-1 py-px text-[10px] text-slate-400">
                  {{ RAG_CATEGORY_LABELS[source.category] }}
                </span>
              </li>
            </ul>
          </template>
        </div>
      </div>
    </div>

    <!-- コンポーザ（パネル下部に常時固定） -->
    <div class="shrink-0 border-t border-slate-200 bg-white p-2 sm:p-3">
      <form class="flex items-end gap-2" @submit.prevent="submit">
        <!-- maxlength はチャット API 契約の1メッセージ上限（4000 文字）に合わせる -->
        <textarea
          ref="composer"
          v-model="draft"
          rows="1"
          maxlength="4000"
          :placeholder="placeholder ?? 'メッセージを入力...'"
          :disabled="disabled"
          class="max-h-36 min-h-[42px] w-full flex-1 resize-none rounded-lg border border-slate-300 bg-white px-3 py-2.5 text-sm text-slate-700 focus:border-indigo-400 focus:outline-none disabled:bg-slate-50 disabled:text-slate-400"
          @keydown.enter="onEnter"
        />
        <div class="flex shrink-0 items-center gap-1.5">
          <button
            v-if="streaming"
            type="button"
            class="inline-flex items-center gap-1.5 rounded-lg border border-slate-300 px-3 py-2.5 text-sm font-medium text-slate-600 hover:bg-slate-50"
            @click="emit('stop')"
          >
            <Square class="h-3.5 w-3.5 fill-current" />
            <span class="hidden sm:inline">停止</span>
          </button>
          <button
            v-else
            type="submit"
            :disabled="disabled || draft.trim() === ''"
            class="inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-3 py-2.5 text-sm font-medium text-white hover:bg-indigo-700 disabled:opacity-50"
          >
            <Send class="h-4 w-4" />
            <span class="hidden sm:inline">送信</span>
          </button>
          <button
            v-if="messages.length > 0"
            type="button"
            :disabled="streaming"
            class="inline-flex items-center gap-1.5 rounded-lg border border-slate-300 px-3 py-2.5 text-sm font-medium text-slate-600 hover:bg-slate-50 disabled:opacity-50"
            @click="onClear"
          >
            <Eraser class="h-4 w-4" />
            <span class="hidden sm:inline">クリア</span>
          </button>
        </div>
      </form>
      <p class="mt-1 hidden text-[11px] text-slate-400 sm:block">
        Enter で送信 / Shift+Enter で改行
      </p>
    </div>
  </div>
</template>
