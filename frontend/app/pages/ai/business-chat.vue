<script setup lang="ts">
/**
 * 業務チャット（/ai/business-chat）。
 *
 * 相談受付デスクのうち chatDomain を持つ部署（システム部・商品管理部・物流部）を選び、
 * その部署のナレッジ（RAG）を参照する AI チャットで業務の質問に回答する。
 * AI 未設定（aiConfigured=false）のときは案内を表示して送信を無効化する。
 */
import { MessagesSquare, Phone, TriangleAlert } from 'lucide-vue-next'
import type { ChatDomain, ContactDesk, ContactDesksResponse, RagStatus } from '~/types/api'

useHead({ title: '業務チャット | UndeuxSales' })

const { get } = useApi()
const { messages, streaming, send, stop, clear } = useChatConversation()

// ---------------------------------------------------------------
// 初期ロード（RAG ステータス + 相談受付デスク）
// ---------------------------------------------------------------
const ragStatus = ref<RagStatus | null>(null)
const desks = ref<ContactDesk[]>([])
const loading = ref(true)
const errorMessage = ref<string | null>(null)

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = null
  try {
    const [statusRes, desksRes] = await Promise.all([
      get<RagStatus>('/api/rag/status'),
      get<ContactDesksResponse>('/api/org-master/contact-desks'),
    ])
    ragStatus.value = statusRes
    desks.value = desksRes.desks
  } catch (error) {
    errorMessage.value = apiErrorMessage(error)
  } finally {
    loading.value = false
  }
}

onMounted(load)

const aiConfigured = computed(() => ragStatus.value?.aiConfigured ?? false)

/** chatDomain を持つ（＝チャット窓口になる）デスク。 */
type ChatDesk = ContactDesk & { chatDomain: ChatDomain }

/** チャット窓口になるデスク（chatDomain 付きのみ・表示順）。同一 domain は先勝ち。 */
const chatDesks = computed<ChatDesk[]>(() => {
  const seen = new Set<ChatDomain>()
  return desks.value
    .filter((desk): desk is ChatDesk => desk.chatDomain !== null)
    .sort((a, b) => a.displayOrder - b.displayOrder)
    .filter((desk) => {
      if (seen.has(desk.chatDomain)) return false
      seen.add(desk.chatDomain)
      return true
    })
})

// ---------------------------------------------------------------
// 部署（ドメイン）選択
// ---------------------------------------------------------------
const selectedDomain = ref<ChatDomain | null>(null)

const selectedDesk = computed(
  () => chatDesks.value.find((desk) => desk.chatDomain === selectedDomain.value) ?? null,
)

function selectDomain(domain: ChatDomain): void {
  if (domain === selectedDomain.value) return
  // 会話がある状態で部署を切り替えると文脈が混ざるため、確認のうえクリアする。
  if (messages.value.length > 0) {
    if (!window.confirm('部署を変更すると会話がクリアされます。よろしいですか？')) return
    clear()
  }
  selectedDomain.value = domain
}

// ---------------------------------------------------------------
// 送信
// ---------------------------------------------------------------
function onSend(text: string): void {
  if (!selectedDomain.value || !aiConfigured.value) return
  send(text, '/api/chat/business', { domain: selectedDomain.value })
}

const emptyHint = computed(() => {
  const label = selectedDomain.value ? CHAT_DOMAIN_LABELS[selectedDomain.value] : ''
  return `${label}に関する質問を入力してください。登録済みナレッジ（マニュアル・基本情報など）を参照して回答します。`
})
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-3">
    <div class="shrink-0">
      <h1 class="text-xl font-bold text-slate-800">業務チャット</h1>
      <p class="text-sm text-slate-500">
        相談したい部署を選ぶと、その部署のナレッジを参照する AI チャットで質問できます。
      </p>
    </div>

    <StatusBlock :loading="loading" :error="errorMessage">
      <div class="shrink-0 space-y-3">
        <!-- AI 未設定の案内 -->
        <div
          v-if="!aiConfigured"
          class="flex items-start gap-2 rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-700"
        >
          <TriangleAlert class="mt-0.5 h-4 w-4 shrink-0" />
          <p>
            <span class="font-medium">AI 未設定</span>：AI が未設定のため、チャットを利用できません。
            運営者にお問い合わせいただくか、下記の電話窓口をご利用ください。
          </p>
        </div>

        <!-- ステップ1: 部署の選択 -->
        <div>
          <p class="mb-1.5 text-xs font-medium text-slate-500">相談する部署を選択</p>
          <div
            v-if="chatDesks.length === 0"
            class="rounded-xl border border-dashed border-slate-200 bg-white p-4 text-sm text-slate-400"
          >
            チャット対応の相談受付デスクが登録されていません。業態・部門マスタで登録してください。
          </div>
          <div v-else class="grid grid-cols-1 gap-2 sm:grid-cols-3">
            <button
              v-for="desk in chatDesks"
              :key="desk.id"
              type="button"
              class="flex flex-col items-start gap-0.5 rounded-xl border p-3 text-left transition-colors"
              :class="desk.chatDomain === selectedDomain
                ? 'border-indigo-600 bg-indigo-50/60 ring-1 ring-indigo-600'
                : 'border-slate-200 bg-white shadow-sm hover:border-indigo-300'"
              @click="selectDomain(desk.chatDomain)"
            >
              <span class="flex items-center gap-1.5 text-sm font-bold text-slate-800">
                <MessagesSquare class="h-4 w-4 text-indigo-500" />
                {{ CHAT_DOMAIN_LABELS[desk.chatDomain] }}
              </span>
              <span class="text-xs text-slate-500">{{ desk.topic }}</span>
              <span v-if="desk.phone" class="flex items-center gap-1 text-xs text-slate-400">
                <Phone class="h-3 w-3" />
                {{ desk.phone }}
              </span>
            </button>
          </div>
        </div>
      </div>

      <!-- ステップ2: チャット -->
      <div v-if="selectedDomain" class="flex min-h-[20rem] flex-1 flex-col">
        <ChatPanel
          class="flex-1"
          :messages="messages"
          :streaming="streaming"
          :disabled="!aiConfigured"
          :placeholder="aiConfigured
            ? `${CHAT_DOMAIN_LABELS[selectedDomain]}への質問を入力...`
            : 'AI 未設定のため送信できません'"
          :empty-hint="emptyHint"
          @send="onSend"
          @stop="stop"
          @clear="clear"
        >
          <template v-if="selectedDesk?.phone" #empty>
            <MessagesSquare class="h-8 w-8 text-indigo-300" />
            <p class="max-w-md text-sm text-slate-400">{{ emptyHint }}</p>
            <p class="text-xs text-slate-400">
              お急ぎの場合は {{ CHAT_DOMAIN_LABELS[selectedDomain] }}（{{ selectedDesk?.phone }}）へお電話ください。
            </p>
          </template>
        </ChatPanel>
      </div>
      <div
        v-else-if="chatDesks.length > 0"
        class="flex flex-1 items-center justify-center rounded-xl border border-dashed border-slate-200 bg-white/60 p-8"
      >
        <p class="text-sm text-slate-400">上から部署を選択すると、チャットを開始できます。</p>
      </div>
    </StatusBlock>
  </div>
</template>
