<script setup lang="ts">
/**
 * 商品導入管理（/mart/introductions）ページ。
 *
 * 商品（業態×記号×品番）単位で導入日（sales_weekly.donyu_date 由来。mart の
 * dim_sku.attributes->>'donyu'）を一覧し、導入時期での絞り込み・把握を行う。
 *
 * フィルタ構成（要望対応）: 業態（タブ切り替え）・部門・ブランド・服種（品番CD）・担当者・
 * キーワード・導入時期（年月のクイック指定）／導入日 From-To。
 * フィルタの選択肢: 業態・部門は /api/filters、ブランド・服種・担当者は
 * /api/mart/introduction-options（mart の商品次元から導出）。
 */
import { ChevronLeft, ChevronRight, Search, RotateCcw } from 'lucide-vue-next'
import type {
  CodeName,
  MartIntroductionOptions,
  MartIntroductionPage,
  MartIntroductionRow,
} from '~/types/api'

useHead({ title: '商品導入管理 | UndeuxSales' })

const { get } = useApi()
const { isBuilt, refreshStatus } = useMart()
// 共有フィルタ（mart-filter スコープ）とは独立した画面専用フィルタ。
// /api/filters の選択肢（業態・部門）だけを再利用する。
const { options: sharedOptions, optionsError, loadOptions } = useFilters('mart-filter')

// ---------------------------------------------------------------
// フィルタ state（業態はタブ＝単一選択、他は複数選択 or テキスト）
// ---------------------------------------------------------------

const businessType = ref<string | null>(null)
const departments = ref<string[]>([])
const brands = ref<string[]>([])
const hinbans = ref<string[]>([])
const managers = ref<string[]>([])
const keyword = ref('')
/** 導入時期（YYYY-MM）。指定すると導入日 From/To を当該月の初日〜末日で埋めるクイック入力。 */
const donyuMonth = ref('')
const donyuFrom = ref('')
const donyuTo = ref('')

const order = ref<'desc' | 'asc'>('desc')
const page = ref(1)
const pageSize = 20

const introductionOptions = ref<MartIntroductionOptions | null>(null)
const result = ref<MartIntroductionPage | null>(null)
const loading = ref(true)
const errorMessage = ref<string | null>(null)

const businessTypeTabs = computed(() =>
  (sharedOptions.value?.businessTypes ?? []).map((b) => ({
    value: b.code,
    label: b.name ? (b.shortName ? `${b.name}（${b.shortName}）` : b.name) : b.code,
  })),
)

function toSelectOptions(items: CodeName[]): { value: string; text: string }[] {
  return items.map((item) => ({
    value: item.code,
    text: item.name ? `${item.code}: ${item.name}` : item.code,
  }))
}

const departmentOptions = computed(() => toSelectOptions(sharedOptions.value?.departments ?? []))
const brandOptions = computed(() =>
  (introductionOptions.value?.brands ?? []).map((b) => ({ value: b, text: b })),
)
const hinbanOptions = computed(() =>
  (introductionOptions.value?.hinbans ?? []).map((h) => ({ value: h, text: h })),
)
const managerOptions = computed(() =>
  (introductionOptions.value?.managers ?? []).map((m) => ({ value: m, text: m })),
)

// 導入時期（年月）を選ぶと From/To を当該月の範囲で上書きする（From/To 個別調整も可能）。
watch(donyuMonth, (month) => {
  if (!/^\d{4}-\d{2}$/.test(month)) return
  const [yearText, monthText] = month.split('-') as [string, string]
  const year = Number.parseInt(yearText, 10)
  const monthIndex = Number.parseInt(monthText, 10)
  const lastDay = new Date(year, monthIndex, 0).getDate()
  donyuFrom.value = `${month}-01`
  donyuTo.value = `${month}-${String(lastDay).padStart(2, '0')}`
})

function toQuery(): Record<string, unknown> {
  const query: Record<string, unknown> = {
    order: order.value,
    page: page.value,
    pageSize,
  }
  if (businessType.value) query.businessTypes = [businessType.value]
  if (departments.value.length > 0) query.departments = departments.value
  if (brands.value.length > 0) query.brands = brands.value
  if (hinbans.value.length > 0) query.hinbans = hinbans.value
  if (managers.value.length > 0) query.managers = managers.value
  if (keyword.value.trim()) query.keyword = keyword.value.trim()
  if (donyuFrom.value) query.donyuFrom = donyuFrom.value
  if (donyuTo.value) query.donyuTo = donyuTo.value
  return query
}

/**
 * 直近に実行した検索で導入日フィルタが適用されていたか。
 * 0件時の再構築案内は「入力欄の現在値」ではなく「適用済みの条件」を基準に判定する
 * （0件表示後に入力欄を編集しただけで案内が出入りしないように）。
 */
const appliedDonyuFilter = ref(false)

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = null
  try {
    await refreshStatus()
    if (!isBuilt.value) {
      result.value = null
      return
    }
    appliedDonyuFilter.value = donyuFrom.value !== '' || donyuTo.value !== ''
    result.value = await get<MartIntroductionPage>('/api/mart/introductions', toQuery())
  } catch (error) {
    errorMessage.value = apiErrorMessage(error)
  } finally {
    loading.value = false
  }
}

function reload(): void {
  page.value = 1
  void load()
}

function resetFilters(): void {
  businessType.value = null
  departments.value = []
  brands.value = []
  hinbans.value = []
  managers.value = []
  keyword.value = ''
  donyuMonth.value = ''
  donyuFrom.value = ''
  donyuTo.value = ''
  reload()
}

const initialized = ref(false)

// 業態タブの切り替えは即時再検索（タブUIの期待動作）。
watch(businessType, () => {
  if (initialized.value) reload()
})

const totalPages = computed(() => {
  const total = result.value?.totalCount ?? 0
  return total === 0 ? 1 : Math.ceil(total / pageSize)
})

function changePage(delta: number): void {
  const next = page.value + delta
  if (next >= 1 && next <= totalPages.value) {
    page.value = next
    void load()
  }
}

// 導入日が全行未設定（旧 mart データ）の場合、再構築を促す。
const needsRebuildHint = computed(() => {
  const items = result.value?.items ?? []
  return items.length > 0 && items.every((item) => item.donyuDate === null)
})

// 導入日で絞り込んで0件の場合、旧 mart データ（導入日未反映）の可能性がある。
// 「絞り込み過ぎ」との切り分けができないため、可能性として案内する。
const emptyWithDonyuFilter = computed(
  () => (result.value?.items.length ?? 0) === 0 && appliedDonyuFilter.value,
)

const columns = [
  { key: 'thumbnail', label: '画像', format: (_row: MartIntroductionRow) => '' },
  { key: 'productCode', label: '品番CD（服種）' },
  {
    key: 'productName',
    label: '商品名',
    format: (row: MartIntroductionRow) => row.productName ?? '-',
  },
  { key: 'brand', label: 'ブランド', format: (row: MartIntroductionRow) => row.brand ?? '-' },
  { key: 'manager', label: '担当者', format: (row: MartIntroductionRow) => row.manager ?? '-' },
  {
    key: 'department',
    label: '部門',
    format: (row: MartIntroductionRow) =>
      row.departmentName ?? row.departmentCode ?? '-',
  },
  {
    key: 'businessType',
    label: '業態',
    format: (row: MartIntroductionRow) => row.channelName ?? row.channelCode,
  },
  {
    key: 'donyuDate',
    label: '導入日',
    format: (row: MartIntroductionRow) => formatDate(row.donyuDate),
  },
  {
    key: 'skuCount',
    label: 'SKU数',
    align: 'right' as const,
    format: (row: MartIntroductionRow) => formatNumber(row.skuCount),
  },
  {
    key: 'salesQuantity',
    label: '売上数量（全期間）',
    align: 'right' as const,
    format: (row: MartIntroductionRow) => formatNumber(row.salesQuantity),
  },
]

onMounted(async () => {
  await loadOptions()
  // 選択肢の取得失敗は検索自体を妨げない（非ブロッキング）。
  try {
    introductionOptions.value = await get<MartIntroductionOptions>(
      '/api/mart/introduction-options',
    )
  } catch {
    introductionOptions.value = null
  }
  await load()
  initialized.value = true
})
</script>

<template>
  <div class="space-y-4">
    <div>
      <h1 class="text-xl font-bold text-slate-800">商品導入管理</h1>
      <p class="text-sm text-slate-500">
        商品（業態×記号×品番）単位の導入日を一覧します。導入時期・導入日 From-To で絞り込み、
        新規導入の状況を把握できます（導入日は売上参照データの donyu_date 由来）。
      </p>
    </div>

    <!-- 業態（タブ切り替え） -->
    <TabSwitcher
      :options="businessTypeTabs"
      :model-value="businessType"
      aria-label="業態"
      @update:model-value="(v) => (businessType = v)"
    />

    <CollapsiblePanel title="フィルター">
      <p
        v-if="optionsError"
        class="mb-3 rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-700"
      >
        フィルタ選択肢の取得に失敗しました: {{ optionsError }}
      </p>

      <div class="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
        <MultiSelect v-model="departments" label="部門" :options="departmentOptions" />
        <MultiSelect v-model="brands" label="ブランド" :options="brandOptions" />
        <MultiSelect v-model="hinbans" label="服種（品番CD）" :options="hinbanOptions" />
        <MultiSelect v-model="managers" label="担当者" :options="managerOptions" />
        <div>
          <label class="mb-1 block text-xs font-medium text-slate-500">キーワード</label>
          <input
            v-model="keyword"
            type="search"
            placeholder="品番・記号・商品名・ブランド・担当者"
            class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-700"
            @keydown.enter="reload"
          >
        </div>
        <div>
          <label class="mb-1 block text-xs font-medium text-slate-500">
            導入時期（年月で一括指定）
          </label>
          <input
            v-model="donyuMonth"
            type="month"
            class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-700"
          >
        </div>
        <div>
          <label class="mb-1 block text-xs font-medium text-slate-500">導入日 From</label>
          <input
            v-model="donyuFrom"
            type="date"
            class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-700"
          >
        </div>
        <div>
          <label class="mb-1 block text-xs font-medium text-slate-500">導入日 To</label>
          <input
            v-model="donyuTo"
            type="date"
            class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-700"
          >
        </div>
        <div>
          <label class="mb-1 block text-xs font-medium text-slate-500">並び順（導入日）</label>
          <select
            v-model="order"
            class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-700"
          >
            <option value="desc">新しい順</option>
            <option value="asc">古い順</option>
          </select>
        </div>
      </div>

      <div class="mt-4 flex flex-wrap gap-2">
        <button
          type="button"
          class="flex items-center gap-1.5 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700"
          @click="reload"
        >
          <Search class="h-4 w-4" />
          適用
        </button>
        <button
          type="button"
          class="flex items-center gap-1.5 rounded-lg border border-slate-300 px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50"
          @click="resetFilters"
        >
          <RotateCcw class="h-4 w-4" />
          リセット
        </button>
      </div>
    </CollapsiblePanel>

    <StatusBlock :loading="loading" :error="errorMessage">
      <MartNotBuiltNotice v-if="!isBuilt" />
      <div
        v-else-if="(result?.items.length ?? 0) === 0"
        class="rounded-xl border border-slate-200 bg-white p-8 text-center text-sm text-slate-400"
      >
        該当する商品がありません。フィルタを見直してください。
        <p v-if="emptyWithDonyuFilter" class="mt-2 text-xs text-amber-600">
          ※ mart が改修前に構築されたままの場合、導入日が未反映で0件になることがあります。
          全社サマリーから「mart を再構築」を実行してからお試しください。
        </p>
      </div>
      <div v-else class="space-y-3">
        <p
          v-if="needsRebuildHint"
          class="rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-700"
        >
          導入日が未反映です。全社サマリーから「mart を再構築」を実行すると導入日が反映されます。
        </p>

        <DataTable
          :columns="columns"
          :rows="result?.items ?? []"
          :row-key="(row: MartIntroductionRow) => `${row.channelCode}|${row.productSign}|${row.productCode}`"
        >
          <template #thumbnail="{ row }">
            <div class="h-10 w-10 overflow-hidden rounded">
              <ProductImage
                :src="(row as MartIntroductionRow).primaryImageUrl"
                :alt="(row as MartIntroductionRow).productName ?? (row as MartIntroductionRow).productCode"
                icon-class="h-4 w-4"
                :show-label="false"
              />
            </div>
          </template>
        </DataTable>

        <div class="flex items-center justify-between text-sm text-slate-600">
          <span>全 {{ formatNumber(result?.totalCount ?? 0) }} 件</span>
          <div class="flex items-center gap-2">
            <button
              type="button"
              class="flex items-center gap-1 rounded-lg border border-slate-300 px-2 py-1 disabled:opacity-40"
              :disabled="page <= 1"
              @click="changePage(-1)"
            >
              <ChevronLeft class="h-4 w-4" />
              前へ
            </button>
            <span>{{ page }} / {{ totalPages }}</span>
            <button
              type="button"
              class="flex items-center gap-1 rounded-lg border border-slate-300 px-2 py-1 disabled:opacity-40"
              :disabled="page >= totalPages"
              @click="changePage(1)"
            >
              次へ
              <ChevronRight class="h-4 w-4" />
            </button>
          </div>
        </div>
      </div>
    </StatusBlock>
  </div>
</template>
