<script setup lang="ts">
/**
 * RAG設定（/ai/rag-settings）。
 *
 * AI チャットが参照するナレッジ（RAG）を管理する。
 * - ステータスカード: AI 設定状態・モデル・スコープ別の登録件数
 * - 運営者RAG設定: operator スコープ（管理者のみ編集可。非管理者は閲覧のみ）
 * - ユーザーRAG設定: user スコープ（全ユーザー編集可）
 * - 検索テスト: チャットが実際に引くナレッジ検索（ハイブリッド）の動作確認
 */
import { BadgeCheck, CircleX, FlaskConical, LoaderCircle } from 'lucide-vue-next'
import type {
  ChatDomain,
  OrgBusinessType,
  OrgMasterResponse,
  RagScope,
  RagSearchResponse,
  RagSearchResult,
  RagStatus,
} from '~/types/api'

useHead({ title: 'RAG設定 | UndeuxSales' })

const { get } = useApi()
const { isOwner } = useAccountType()

// ---------------------------------------------------------------
// 初期ロード（RAG ステータス + 組織マスタ）
// ---------------------------------------------------------------
const ragStatus = ref<RagStatus | null>(null)
const businessTypes = ref<OrgBusinessType[]>([])
const loading = ref(true)
const errorMessage = ref<string | null>(null)

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = null
  try {
    const [statusRes, orgRes] = await Promise.all([
      get<RagStatus>('/api/rag/status'),
      get<OrgMasterResponse>('/api/org-master'),
    ])
    ragStatus.value = statusRes
    businessTypes.value = orgRes.businessTypes
  } catch (error) {
    errorMessage.value = apiErrorMessage(error)
  } finally {
    loading.value = false
  }
}

onMounted(load)

/**
 * ナレッジの増減時にステータスカードの件数を更新する。
 * 補助情報のため取得失敗は無視する（原則4: 非ブロッキング）。
 */
async function refreshStatus(): Promise<void> {
  try {
    ragStatus.value = await get<RagStatus>('/api/rag/status')
  } catch {
    // 件数表示の更新失敗は主要フロー（ナレッジ管理）を妨げない。
  }
}

/** スコープ別の合計件数とカテゴリ内訳の表示文字列。 */
function scopeSummary(scope: RagScope): { total: number; breakdown: string } {
  const counts = (ragStatus.value?.counts ?? []).filter((c) => c.scope === scope)
  const total = counts.reduce((sum, c) => sum + c.count, 0)
  const breakdown = counts
    .filter((c) => c.count > 0)
    .map((c) => `${RAG_CATEGORY_LABELS[c.category]} ${c.count}`)
    .join('・')
  return { total, breakdown }
}

const operatorSummary = computed(() => scopeSummary('operator'))
const userSummary = computed(() => scopeSummary('user'))

// ---------------------------------------------------------------
// 検索テスト
// ---------------------------------------------------------------
type SearchMode = '' | 'business' | 'negotiation'

const searchQuery = ref('')
const searchMode = ref<SearchMode>('')
const searchDomain = ref<ChatDomain>('system')
const searchBtCode = ref('')
const searchDeptCode = ref('')
const searchResults = ref<RagSearchResult[] | null>(null)
const searchLoading = ref(false)
const searchError = ref<string | null>(null)

const searchDeptOptions = computed(() => {
  const bt = businessTypes.value.find((b) => b.code === searchBtCode.value)
  return bt ? [...bt.departments].sort((a, b) => a.displayOrder - b.displayOrder) : []
})

function onSearchModeChange(): void {
  searchResults.value = null
  searchError.value = null
}

async function runSearch(): Promise<void> {
  const query = searchQuery.value.trim()
  if (!query) {
    searchError.value = '検索キーワードを入力してください。'
    return
  }
  searchLoading.value = true
  searchError.value = null
  try {
    const params: Record<string, unknown> = { query, limit: 8 }
    if (searchMode.value === 'business') {
      params.mode = 'business'
      params.domain = searchDomain.value
    } else if (searchMode.value === 'negotiation') {
      params.mode = 'negotiation'
      if (searchBtCode.value) params.businessTypeCode = searchBtCode.value
      if (searchDeptCode.value) params.deptCode = searchDeptCode.value
    }
    const response = await get<RagSearchResponse>('/api/rag/search', params)
    searchResults.value = response.results
  } catch (error) {
    searchError.value = apiErrorMessage(error)
  } finally {
    searchLoading.value = false
  }
}

/** スコア（0〜1想定）→ バー幅（%）。範囲外はクランプする。 */
function scoreWidth(score: number): string {
  return `${Math.max(0, Math.min(1, score)) * 100}%`
}
</script>

<template>
  <div class="space-y-4">
    <div>
      <h1 class="text-xl font-bold text-slate-800">RAG設定</h1>
      <p class="text-sm text-slate-500">
        AI チャット（業務チャット・商談チャット）が参照するナレッジを管理します。
      </p>
    </div>

    <StatusBlock :loading="loading" :error="errorMessage">
      <div class="space-y-4">
        <!-- ステータスカード -->
        <div class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
          <div class="flex flex-wrap items-center gap-x-4 gap-y-2">
            <span
              class="inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-xs font-medium"
              :class="ragStatus?.aiConfigured
                ? 'border-emerald-200 bg-emerald-50 text-emerald-700'
                : 'border-slate-200 bg-slate-100 text-slate-500'"
            >
              <component :is="ragStatus?.aiConfigured ? BadgeCheck : CircleX" class="h-3.5 w-3.5" />
              {{ ragStatus?.aiConfigured ? 'AI 設定済み' : 'AI 未設定' }}
            </span>
            <span class="text-xs text-slate-500">
              チャットモデル: <span class="font-medium text-slate-700">{{ ragStatus?.chatModel ?? '未設定' }}</span>
            </span>
            <span class="text-xs text-slate-500">
              埋め込みモデル: <span class="font-medium text-slate-700">{{ ragStatus?.embeddingModel ?? '未設定' }}</span>
            </span>
          </div>
          <div class="mt-2 grid grid-cols-1 gap-1 text-xs text-slate-500 sm:grid-cols-2">
            <p>
              運営者ナレッジ: <span class="font-medium text-slate-700">{{ formatNumber(operatorSummary.total) }} 件</span>
              <span v-if="operatorSummary.breakdown" class="text-slate-400">（{{ operatorSummary.breakdown }}）</span>
            </p>
            <p>
              ユーザーナレッジ: <span class="font-medium text-slate-700">{{ formatNumber(userSummary.total) }} 件</span>
              <span v-if="userSummary.breakdown" class="text-slate-400">（{{ userSummary.breakdown }}）</span>
            </p>
          </div>
          <p v-if="!ragStatus?.aiConfigured" class="mt-2 text-xs text-amber-600">
            AI が未設定のためチャット応答と画像の内容説明生成は利用できません。ナレッジの登録・整備は可能です。
          </p>
        </div>

        <!-- 運営者RAG設定（管理者のみ編集可） -->
        <RagKnowledgeSection
          scope="operator"
          title="運営者RAG設定"
          description="運営者が全ユーザー向けに提供するナレッジ（マニュアル・基本情報など）です。"
          :can-edit="isOwner"
          :business-types="businessTypes"
          @changed="refreshStatus"
        />

        <!-- ユーザーRAG設定（全ユーザー編集可） -->
        <RagKnowledgeSection
          scope="user"
          title="ユーザーRAG設定"
          description="自社で追加するナレッジです。登録するとチャットの回答に反映されます。"
          :can-edit="true"
          :business-types="businessTypes"
          @changed="refreshStatus"
        />

        <!-- 検索テスト -->
        <section class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
          <div class="flex items-center gap-1.5">
            <FlaskConical class="h-4 w-4 text-indigo-500" />
            <h2 class="text-base font-bold text-slate-800">検索テスト</h2>
          </div>
          <p class="mt-0.5 text-xs text-slate-500">
            チャットが回答時に参照するナレッジ検索（ベクター + キーワードのハイブリッド）を試せます。
          </p>

          <div class="mt-3 grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-4">
            <div class="sm:col-span-2">
              <label class="mb-1 block text-xs font-medium text-slate-500">検索キーワード</label>
              <input
                v-model="searchQuery"
                type="search"
                placeholder="例: 納品リードタイム"
                class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm focus:border-indigo-400 focus:outline-none"
                @keydown.enter.prevent="runSearch"
              >
            </div>
            <div>
              <label class="mb-1 block text-xs font-medium text-slate-500">モード</label>
              <select
                v-model="searchMode"
                class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
                @change="onSearchModeChange"
              >
                <option value="">指定なし（全件）</option>
                <option value="business">業務チャット</option>
                <option value="negotiation">商談チャット</option>
              </select>
            </div>
            <div v-if="searchMode === 'business'">
              <label class="mb-1 block text-xs font-medium text-slate-500">部署</label>
              <select v-model="searchDomain" class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm">
                <option v-for="domain in CHAT_DOMAINS" :key="domain" :value="domain">
                  {{ CHAT_DOMAIN_LABELS[domain] }}
                </option>
              </select>
            </div>
            <template v-if="searchMode === 'negotiation'">
              <div>
                <label class="mb-1 block text-xs font-medium text-slate-500">業態</label>
                <select
                  v-model="searchBtCode"
                  class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
                  @change="searchDeptCode = ''"
                >
                  <option value="">指定なし</option>
                  <option v-for="bt in businessTypes" :key="bt.code" :value="bt.code">
                    {{ bt.displayName ?? bt.code }}
                  </option>
                </select>
              </div>
              <div>
                <label class="mb-1 block text-xs font-medium text-slate-500">部門</label>
                <select v-model="searchDeptCode" class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm">
                  <option value="">指定なし</option>
                  <option v-for="dept in searchDeptOptions" :key="dept.id" :value="dept.deptCode">
                    {{ dept.deptCode }} {{ dept.deptName }}
                  </option>
                </select>
              </div>
            </template>
          </div>

          <div class="mt-3 flex items-center gap-2">
            <button
              type="button"
              :disabled="searchLoading"
              class="inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700 disabled:opacity-50"
              @click="runSearch"
            >
              <LoaderCircle v-if="searchLoading" class="h-4 w-4 animate-spin" />
              <FlaskConical v-else class="h-4 w-4" />
              実行
            </button>
            <p v-if="searchError" class="text-xs text-rose-600">{{ searchError }}</p>
          </div>

          <!-- 検索結果 -->
          <div v-if="searchResults !== null" class="mt-3">
            <p v-if="searchResults.length === 0" class="rounded-lg border border-dashed border-slate-200 p-4 text-center text-sm text-slate-400">
              該当するナレッジが見つかりませんでした。
            </p>
            <ul v-else class="space-y-2">
              <li
                v-for="result in searchResults"
                :key="result.chunkId"
                class="rounded-lg border border-slate-200 p-3"
              >
                <div class="flex flex-wrap items-center gap-1.5">
                  <span class="text-sm font-medium text-slate-800">{{ result.title }}</span>
                  <span class="text-xs text-slate-400">#{{ result.seq }}</span>
                  <span class="rounded-full border border-indigo-200 bg-indigo-50 px-2 py-0.5 text-[11px] text-indigo-700">
                    {{ RAG_CATEGORY_LABELS[result.category] }}
                  </span>
                  <span
                    v-if="result.businessTypeCode || result.deptCode"
                    class="rounded-full border border-violet-200 bg-violet-50 px-2 py-0.5 text-[11px] text-violet-700"
                  >
                    {{ result.businessTypeCode }}<template v-if="result.deptCode"> / {{ result.deptCode }}</template>
                  </span>
                </div>
                <p class="mt-1 line-clamp-2 text-xs leading-relaxed text-slate-500">{{ result.snippet }}</p>
                <div class="mt-2 grid grid-cols-1 gap-1 sm:grid-cols-3">
                  <div class="flex items-center gap-1.5 text-[11px] text-slate-500">
                    <span class="w-16 shrink-0">総合 {{ result.score.toFixed(2) }}</span>
                    <div class="h-1.5 w-full max-w-28 overflow-hidden rounded-full bg-slate-100">
                      <div class="h-full rounded-full bg-indigo-500" :style="{ width: scoreWidth(result.score) }" />
                    </div>
                  </div>
                  <div class="flex items-center gap-1.5 text-[11px] text-slate-500">
                    <span class="w-16 shrink-0">ベクター {{ result.vectorScore.toFixed(2) }}</span>
                    <div class="h-1.5 w-full max-w-28 overflow-hidden rounded-full bg-slate-100">
                      <div class="h-full rounded-full bg-sky-500" :style="{ width: scoreWidth(result.vectorScore) }" />
                    </div>
                  </div>
                  <div class="flex items-center gap-1.5 text-[11px] text-slate-500">
                    <span class="w-16 shrink-0">キーワード {{ result.lexicalScore.toFixed(2) }}</span>
                    <div class="h-1.5 w-full max-w-28 overflow-hidden rounded-full bg-slate-100">
                      <div class="h-full rounded-full bg-emerald-500" :style="{ width: scoreWidth(result.lexicalScore) }" />
                    </div>
                  </div>
                </div>
              </li>
            </ul>
          </div>
        </section>
      </div>
    </StatusBlock>
  </div>
</template>
