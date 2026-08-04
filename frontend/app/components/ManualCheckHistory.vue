<script setup lang="ts">
import { RefreshCw } from 'lucide-vue-next'
import type { ManualCheckPage, ManualCheckSummary } from '~/types/api'

/**
 * 手入力チェックの履歴一覧（作成日時降順・ページング）。
 * 既存の副資材チェック履歴（index.vue）と同じ流儀（リクエスト世代・手動更新）。
 * 行クリックで結果詳細（/subsidiary-check/manual/{checkId}）へ遷移する。
 */
const { get } = useApi()

const PAGE_SIZE = 20

const page = ref(1)
const history = ref<ManualCheckPage | null>(null)
const loading = ref(false)
const error = ref<string | null>(null)
const loaded = ref(false)
let requestSeq = 0

async function loadHistory(): Promise<void> {
  const seq = ++requestSeq
  loading.value = true
  error.value = null
  try {
    const result = await get<ManualCheckPage>('/api/subsidiary-check/manual', {
      page: page.value,
      pageSize: PAGE_SIZE,
    })
    if (seq !== requestSeq) return
    history.value = result
    loaded.value = true
  } catch (err) {
    if (seq !== requestSeq) return
    console.error('[manual-check] 履歴の取得に失敗しました:', err)
    error.value = apiErrorMessage(err)
  } finally {
    if (seq === requestSeq) loading.value = false
  }
}

function findingsText(row: ManualCheckSummary): string {
  if (row.status !== 'completed') return '—'
  const parts: string[] = []
  if (row.mismatchCount > 0) parts.push(`相違 ${formatNumber(row.mismatchCount)}`)
  if (row.warnCount > 0) parts.push(`要確認 ${formatNumber(row.warnCount)}`)
  if (parts.length === 0) return '一致'
  return parts.join(' / ')
}

const columns = [
  { key: 'productLabel', label: '対象', frozen: true, width: 200 },
  { key: 'patternName', label: 'パターン', format: (row: ManualCheckSummary) => row.patternName || '—' },
  { key: 'status', label: '状態' },
  { key: 'judgment', label: '判定' },
  { key: 'findings', label: '突合', format: findingsText },
  { key: 'images', label: '画像', format: (row: ManualCheckSummary) => `${formatNumber(row.imageCount)} 枚` },
  { key: 'createdBy', label: '実行者', format: (row: ManualCheckSummary) => row.createdBy || '—' },
  { key: 'createdAt', label: '作成日時', format: (row: ManualCheckSummary) => formatDateTime(row.createdAt) },
]

function handleRowClick(row: ManualCheckSummary): void {
  void navigateTo(`/subsidiary-check/manual/${row.checkId}`)
}

const pageInfo = computed(() => {
  const loadedPage = history.value
  const total = loadedPage?.totalCount ?? 0
  if (!loadedPage || total === 0) return '0 件'
  const size = loadedPage.pageSize > 0 ? loadedPage.pageSize : PAGE_SIZE
  const from = (loadedPage.page - 1) * size + 1
  const to = Math.min(loadedPage.page * size, total)
  return `${formatNumber(from)}〜${formatNumber(to)} / ${formatNumber(total)} 件`
})

const hasNextPage = computed(() => page.value * PAGE_SIZE < (history.value?.totalCount ?? 0))

async function movePage(delta: number): Promise<void> {
  const next = page.value + delta
  if (next < 1 || (delta > 0 && !hasNextPage.value)) return
  page.value = next
  await loadHistory()
}

onMounted(() => {
  if (!loaded.value && !loading.value) void loadHistory()
})
</script>

<template>
  <section aria-label="手入力チェック履歴" class="space-y-3">
    <div class="flex justify-end">
      <button
        type="button"
        class="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-xs font-medium text-slate-600 transition-colors hover:border-indigo-300 hover:text-indigo-700 disabled:cursor-not-allowed disabled:opacity-40"
        :disabled="loading"
        @click="loadHistory"
      >
        <RefreshCw class="h-3.5 w-3.5 shrink-0" :class="loading ? 'animate-spin' : ''" aria-hidden="true" />
        {{ loading ? '更新中...' : '更新' }}
      </button>
    </div>

    <StatusBlock
      :loading="!loaded && !error"
      :error="error"
      :empty="(history?.items.length ?? 0) === 0"
      empty-message="手入力チェックの履歴がありません。「手入力チェック」タブから実行できます。"
    >
      <div class="space-y-3">
        <p class="text-xs text-slate-400">{{ pageInfo }}</p>

        <DataTable
          :columns="columns"
          :rows="history?.items ?? []"
          :row-key="(row: ManualCheckSummary) => row.checkId"
          clickable
          @row-click="handleRowClick"
        >
          <template #productLabel="{ row }">
            <span
              class="font-bold"
              :class="(row as ManualCheckSummary).productLabel ? 'text-slate-800' : 'font-normal text-slate-400'"
            >
              {{ (row as ManualCheckSummary).productLabel || '（ラベル未設定）' }}
            </span>
          </template>
          <template #status="{ row }">
            <span
              class="inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-xs font-medium"
              :class="subsidiaryCheckStatusPresentation((row as ManualCheckSummary).status).className"
            >
              <component
                :is="subsidiaryCheckStatusPresentation((row as ManualCheckSummary).status).icon"
                class="h-3 w-3"
                :class="(row as ManualCheckSummary).status === 'processing' ? 'animate-spin' : ''"
                aria-hidden="true"
              />
              {{ subsidiaryCheckStatusPresentation((row as ManualCheckSummary).status).label }}
            </span>
          </template>
          <template #judgment="{ row }">
            <span
              v-if="(row as ManualCheckSummary).judgment"
              class="inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-xs font-medium"
              :class="subsidiaryCheckJudgmentPresentation((row as ManualCheckSummary).judgment!).className"
            >
              <component
                :is="subsidiaryCheckJudgmentPresentation((row as ManualCheckSummary).judgment!).icon"
                class="h-3 w-3"
                aria-hidden="true"
              />
              {{ subsidiaryCheckJudgmentPresentation((row as ManualCheckSummary).judgment!).label }}
            </span>
            <span v-else class="text-xs text-slate-300">—</span>
          </template>
        </DataTable>

        <div v-if="(history?.totalCount ?? 0) > PAGE_SIZE" class="flex items-center justify-center gap-3">
          <button
            type="button"
            class="rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-xs font-medium text-slate-600 transition-colors hover:border-indigo-300 disabled:cursor-not-allowed disabled:opacity-40"
            :disabled="page <= 1 || loading"
            @click="movePage(-1)"
          >
            ← 前へ
          </button>
          <span class="text-xs text-slate-500">{{ pageInfo }}</span>
          <button
            type="button"
            class="rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-xs font-medium text-slate-600 transition-colors hover:border-indigo-300 disabled:cursor-not-allowed disabled:opacity-40"
            :disabled="!hasNextPage || loading"
            @click="movePage(1)"
          >
            次へ →
          </button>
        </div>
      </div>
    </StatusBlock>
  </section>
</template>
