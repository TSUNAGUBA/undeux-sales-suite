<script setup lang="ts">
import {
  BookOpenText,
  CircleX,
  History,
  ImagePlus,
  LoaderCircle,
  RefreshCw,
  Search,
  Sparkles,
  Trash2,
  X,
} from 'lucide-vue-next'
import type { Component, Ref } from 'vue'
import type {
  ApiError,
  MasterProductPage,
  MasterProductSummary,
  SubsidiaryCheckDetail,
  SubsidiaryCheckImageKind,
  SubsidiaryCheckPage,
  SubsidiaryCheckSummary,
  SubsidiaryRuleBook,
  SubsidiaryRuleItem,
} from '~/types/api'

/**
 * 副資材チェック（サプライヤーの出荷前副資材検品）。
 * 指示書（工程表 FAX 等）と実物タグの画像を AI が突き合わせ、
 * レイアウト・順番・内容の3要素をチェックする。
 *
 * 構成: ページ内3タブ（チェック履歴 / 新規チェック / ルールブック）。
 * タブは ?tab= クエリと同期する（在庫マネジメント inventory.vue で確立済みのパターン）。
 * 判定・状態の表示カタログは utils/subsidiaryCheck.ts に一元化（結果詳細ページと共用）。
 */
useHead({ title: '副資材チェック | UndeuxSales' })

const { get, post } = useApi()
const route = useRoute()
const router = useRouter()

// ---- ページ内タブ（?tab= と同期） ----

const TABS: ReadonlyArray<{ key: TabKey; label: string; icon: Component }> = [
  { key: 'history', label: 'チェック履歴', icon: History },
  { key: 'new', label: '新規チェック', icon: ImagePlus },
  { key: 'rules', label: 'ルールブック', icon: BookOpenText },
]
type TabKey = 'history' | 'new' | 'rules'

function parseTab(value: unknown): TabKey {
  return value === 'new' || value === 'rules' ? value : 'history'
}

/** URL がタブ状態の SoT（直リンク・戻る/進むに追従）。 */
const activeTab = computed<TabKey>(() => parseTab(route.query.tab))

function setTab(tab: TabKey): void {
  if (tab === activeTab.value) return
  // タブはビュー状態であり「戻る」の単位にしない（replace で履歴を汚さない）。
  // 既定タブはクエリを外して素の URL を保つ。
  const query = { ...route.query }
  if (tab === 'history') {
    delete query.tab
  } else {
    query.tab = tab
  }
  void router.replace({ query })
}

// ---- チェック履歴タブ（GET /api/subsidiary-check） ----

const PAGE_SIZE = 20

const historyPage = ref(1)
const history = ref<SubsidiaryCheckPage | null>(null)
const historyLoading = ref(false)
const historyError = ref<string | null>(null)
const historyLoaded = ref(false)
// 連打・高速切替で古い応答が後着しても表示を上書きしないためのリクエスト世代
// （inventory.vue 等で確立済みのパターン）。
let historyRequestSeq = 0

async function loadHistory(): Promise<void> {
  const seq = ++historyRequestSeq
  historyLoading.value = true
  historyError.value = null
  try {
    const page = await get<SubsidiaryCheckPage>('/api/subsidiary-check', {
      page: historyPage.value,
      pageSize: PAGE_SIZE,
    })
    if (seq !== historyRequestSeq) return
    history.value = page
    historyLoaded.value = true
  } catch (error) {
    if (seq !== historyRequestSeq) return
    console.error('[subsidiary-check] チェック履歴の取得に失敗しました:', error)
    historyError.value = apiErrorMessage(error)
  } finally {
    if (seq === historyRequestSeq) {
      historyLoading.value = false
    }
  }
}

/** 指摘列の表示（completed 以外は集計前のため —）。 */
function findingsText(row: SubsidiaryCheckSummary): string {
  if (row.status !== 'completed') return '—'
  const parts: string[] = []
  if (row.failCount > 0) parts.push(`要修正 ${formatNumber(row.failCount)}`)
  if (row.warnCount > 0) parts.push(`要確認 ${formatNumber(row.warnCount)}`)
  return parts.length > 0 ? parts.join(' / ') : '指摘なし'
}

// 横スクロール時、対象商品（識別列）を左固定する（DataTable の frozen 対応・既存規約）。
const historyColumns = [
  { key: 'productLabel', label: '対象商品', frozen: true, width: 200 },
  { key: 'status', label: '状態' },
  { key: 'judgment', label: '判定' },
  { key: 'findings', label: '指摘', format: findingsText },
  {
    key: 'images',
    label: '画像',
    format: (row: SubsidiaryCheckSummary) =>
      `指示書 ${formatNumber(row.instructionImageCount)}・タグ ${formatNumber(row.tagImageCount)}`,
  },
  { key: 'createdBy', label: '実行者', format: (row: SubsidiaryCheckSummary) => row.createdBy || '—' },
  { key: 'createdAt', label: '作成日時', format: (row: SubsidiaryCheckSummary) => formatDateTime(row.createdAt) },
]

function handleHistoryRowClick(row: SubsidiaryCheckSummary): void {
  void navigateTo(`/subsidiary-check/${row.checkId}`)
}

// ---- 履歴のページング（inventory.vue のパターン） ----

const historyPageInfo = computed(() => {
  const total = history.value?.totalCount ?? 0
  if (total === 0) return '0 件'
  const from = (historyPage.value - 1) * PAGE_SIZE + 1
  const to = Math.min(historyPage.value * PAGE_SIZE, total)
  return `${formatNumber(from)}〜${formatNumber(to)} / ${formatNumber(total)} 件`
})

const hasNextHistoryPage = computed(
  () => historyPage.value * PAGE_SIZE < (history.value?.totalCount ?? 0),
)

async function moveHistoryPage(delta: number): Promise<void> {
  const next = historyPage.value + delta
  if (next < 1 || (delta > 0 && !hasNextHistoryPage.value)) return
  historyPage.value = next
  await loadHistory()
}

// ---- 新規チェックタブ: 商品マスタ検索（任意・選択解除可） ----

const SEARCH_PAGE_SIZE = 8

const productKeyword = ref('')
const productResults = ref<MasterProductPage | null>(null)
const productSearchLoading = ref(false)
const productSearchError = ref<string | null>(null)
/** 検索を1回でも実行したか（0件メッセージの出し分け用）。 */
const productSearched = ref(false)
let productSearchSeq = 0

/** 選択中の商品（productLabel はチェック記録に残す表示用スナップショット）。 */
const selectedProduct = ref<{ productId: string; label: string } | null>(null)

async function searchProducts(): Promise<void> {
  const seq = ++productSearchSeq
  productSearchLoading.value = true
  productSearchError.value = null
  try {
    // 商品マスタ一覧と同じ検索パラメータ（search=商品名・記号・品番・ブランド）を使う。
    const query: Record<string, unknown> = { page: 1, pageSize: SEARCH_PAGE_SIZE }
    if (productKeyword.value.trim()) query.search = productKeyword.value.trim()
    const page = await get<MasterProductPage>('/api/product-master', query)
    if (seq !== productSearchSeq) return
    productResults.value = page
    productSearched.value = true
  } catch (error) {
    if (seq !== productSearchSeq) return
    console.error('[subsidiary-check] 商品マスタの検索に失敗しました:', error)
    productSearchError.value = apiErrorMessage(error)
  } finally {
    if (seq === productSearchSeq) {
      productSearchLoading.value = false
    }
  }
}

function selectProduct(product: MasterProductSummary): void {
  // サーバは商品ラベルを SUBSIDIARY_PRODUCT_LABEL_MAX_LENGTH 文字までしか受け付けない
  // （UNDX-REQ-001）。商品名が長いマスタでも 400 にならないよう、送信前に同値で切り詰める。
  selectedProduct.value = {
    productId: product.productId,
    label: `${product.productTypeCrd} ${product.productName}`.slice(
      0,
      SUBSIDIARY_PRODUCT_LABEL_MAX_LENGTH,
    ),
  }
  // 選択後は候補リストを閉じる（誤タップ防止。再検索でいつでも開き直せる）。
  productResults.value = null
  productSearched.value = false
}

function clearProduct(): void {
  selectedProduct.value = null
}

// ---- 新規チェックタブ: 画像アップロード（クライアント検証 + プレビュー） ----

interface UploadImage {
  file: File
  /** サムネイル表示用 objectURL（削除時・アンマウント時に必ず revoke する）。 */
  previewUrl: string
}

const instructionImages = ref<UploadImage[]>([])
const tagImages = ref<UploadImage[]>([])
/** ファイル選択時の検証エラー（選択のたびに置き換える）。 */
const fileErrors = ref<string[]>([])

/** アップロード枠の表示定義（指示書 / タグの2枠を同一マークアップで描画する）。 */
const uploadAreas = computed(() => [
  {
    kind: 'instruction' as const,
    title: '指示書画像',
    note: `工程表・依頼FAX等（1〜${SUBSIDIARY_INSTRUCTION_MAX_COUNT}枚・必須）`,
    images: instructionImages.value,
  },
  {
    kind: 'tag' as const,
    title: 'タグ画像',
    note: `実物タグ・下げ札・品質表示（1〜${SUBSIDIARY_TAG_MAX_COUNT}枚・必須）`,
    images: tagImages.value,
  },
])

function imagesOf(kind: SubsidiaryCheckImageKind): Ref<UploadImage[]> {
  return kind === 'instruction' ? instructionImages : tagImages
}

function maxCountOf(kind: SubsidiaryCheckImageKind): number {
  return kind === 'instruction' ? SUBSIDIARY_INSTRUCTION_MAX_COUNT : SUBSIDIARY_TAG_MAX_COUNT
}

/** 選択済み全画像（指示書＋タグ）の合計サイズ。 */
function totalSelectedBytes(): number {
  return [...instructionImages.value, ...tagImages.value].reduce(
    (sum, image) => sum + image.file.size,
    0,
  )
}

/**
 * ファイル追加。形式（jpeg/png）・サイズ（各5MB・合計20MB）・枚数上限をクライアント側でも検証し、
 * 通ったものだけプレビュー付きで追加する（検証の SoT はバックエンド。ここは即時フィードバック用）。
 */
function addFiles(kind: SubsidiaryCheckImageKind, fileList: FileList | null): void {
  const errors: string[] = []
  const target = imagesOf(kind)
  const max = maxCountOf(kind)
  const kindLabel = SUBSIDIARY_CHECK_IMAGE_KINDS[kind].label
  for (const file of Array.from(fileList ?? [])) {
    if (!(SUBSIDIARY_IMAGE_TYPES as readonly string[]).includes(file.type)) {
      errors.push(`${file.name}: JPEG / PNG 形式の画像のみアップロードできます。`)
      continue
    }
    if (file.size > SUBSIDIARY_IMAGE_MAX_BYTES) {
      errors.push(`${file.name}: ファイルサイズが上限（${formatFileSize(SUBSIDIARY_IMAGE_MAX_BYTES)}）を超えています。`)
      continue
    }
    if (totalSelectedBytes() + file.size > SUBSIDIARY_TOTAL_IMAGE_MAX_BYTES) {
      errors.push(
        `${file.name}: 画像の合計サイズを ${formatFileSize(SUBSIDIARY_TOTAL_IMAGE_MAX_BYTES)} 以内にしてください`
        + `（現在 ${formatFileSize(totalSelectedBytes())}）。`,
      )
      continue
    }
    if (target.value.length >= max) {
      errors.push(`${kindLabel}画像は最大 ${max} 枚までです（超過分は追加されません）。`)
      break
    }
    target.value = [...target.value, { file, previewUrl: URL.createObjectURL(file) }]
  }
  fileErrors.value = errors
}

function onFileChange(kind: SubsidiaryCheckImageKind, event: Event): void {
  const input = event.target as HTMLInputElement
  addFiles(kind, input.files)
  // 同じファイルを削除→再選択できるよう input はクリアする。
  input.value = ''
}

function removeImage(kind: SubsidiaryCheckImageKind, index: number): void {
  const target = imagesOf(kind)
  const removed = target.value[index]
  if (!removed) return
  URL.revokeObjectURL(removed.previewUrl)
  target.value = target.value.filter((_, i) => i !== index)
}

onBeforeUnmount(() => {
  // プレビュー用 objectURL のリーク防止（ページ離脱時に全件 revoke）。
  for (const image of [...instructionImages.value, ...tagImages.value]) {
    URL.revokeObjectURL(image.previewUrl)
  }
})

// ---- 新規チェックタブ: 実行（multipart POST → 詳細へ遷移） ----

const submitting = ref(false)
/** 実行前のクライアント検証エラー（枚数不足など）。 */
const formError = ref<string | null>(null)
/** サーバエラー（imports.vue と同様の ApiError パネルで表示）。 */
const submitError = ref<ApiError | null>(null)

const canSubmit = computed(
  () => instructionImages.value.length > 0 && tagImages.value.length > 0 && !submitting.value,
)

async function runCheck(): Promise<void> {
  if (submitting.value) return
  formError.value = null
  submitError.value = null
  if (instructionImages.value.length < 1 || instructionImages.value.length > SUBSIDIARY_INSTRUCTION_MAX_COUNT) {
    formError.value = `指示書画像を 1〜${SUBSIDIARY_INSTRUCTION_MAX_COUNT} 枚アップロードしてください。`
    return
  }
  if (tagImages.value.length < 1 || tagImages.value.length > SUBSIDIARY_TAG_MAX_COUNT) {
    formError.value = `タグ画像を 1〜${SUBSIDIARY_TAG_MAX_COUNT} 枚アップロードしてください。`
    return
  }
  // 合計サイズの最終ガード（追加時検証と同値。サーバは UNDX-REQ-008 で拒否する）。
  if (totalSelectedBytes() > SUBSIDIARY_TOTAL_IMAGE_MAX_BYTES) {
    formError.value =
      `画像の合計サイズを ${formatFileSize(SUBSIDIARY_TOTAL_IMAGE_MAX_BYTES)} 以内にしてください`
      + `（現在 ${formatFileSize(totalSelectedBytes())}）。`
    return
  }
  submitting.value = true
  try {
    const formData = new FormData()
    if (selectedProduct.value) {
      formData.append('productId', selectedProduct.value.productId)
      formData.append('productLabel', selectedProduct.value.label)
    }
    for (const image of instructionImages.value) {
      formData.append('instructionImages', image.file, image.file.name)
    }
    for (const image of tagImages.value) {
      formData.append('tagImages', image.file, image.file.name)
    }
    // POST は登録（レコード＋画像の永続化）だけを同期で行い、status=processing の Detail を
    // 即座に返す（AI 実行はバックグラウンド。共用リバースプロキシのタイムアウト回避）。
    // 結果詳細へ遷移し、そちらの自動ポーリングで completed / failed（＋再実行導線）を見せる。
    const detail = await post<SubsidiaryCheckDetail>('/api/subsidiary-check', formData)
    await navigateTo(`/subsidiary-check/${detail.summary.checkId}`)
  } catch (error) {
    console.error('[subsidiary-check] AIチェックの実行に失敗しました:', error)
    submitError.value =
      extractApiError(error) ?? { errorCode: 'UNKNOWN', summary: apiErrorMessage(error), remedy: '' }
  } finally {
    submitting.value = false
  }
}

// ---- ルールブックタブ（GET /api/subsidiary-check/rules） ----

const ruleBook = ref<SubsidiaryRuleBook | null>(null)
const rulesLoading = ref(false)
const rulesError = ref<string | null>(null)
const rulesLoaded = ref(false)
let rulesRequestSeq = 0

async function loadRules(): Promise<void> {
  const seq = ++rulesRequestSeq
  rulesLoading.value = true
  rulesError.value = null
  try {
    const book = await get<SubsidiaryRuleBook>('/api/subsidiary-check/rules')
    if (seq !== rulesRequestSeq) return
    ruleBook.value = book
    rulesLoaded.value = true
  } catch (error) {
    if (seq !== rulesRequestSeq) return
    console.error('[subsidiary-check] ルールブックの取得に失敗しました:', error)
    rulesError.value = apiErrorMessage(error)
  } finally {
    if (seq === rulesRequestSeq) {
      rulesLoading.value = false
    }
  }
}

const ruleColumns = [
  { key: 'name', label: '項目' },
  { key: 'description', label: '内容' },
  { key: 'source', label: '出典', format: (row: SubsidiaryRuleItem) => row.source ?? '—' },
]

// ---- タブ単位の遅延ロード ----

async function ensureTabLoaded(tab: TabKey): Promise<void> {
  if (tab === 'history' && !historyLoaded.value && !historyLoading.value) {
    await loadHistory()
  } else if (tab === 'rules' && !rulesLoaded.value && !rulesLoading.value) {
    await loadRules()
  }
  // 新規チェックタブはサーバ取得不要（商品検索は操作時にオンデマンド実行）。
}

watch(activeTab, (tab) => {
  void ensureTabLoaded(tab)
})

onMounted(() => {
  void ensureTabLoaded(activeTab.value)
})
</script>

<template>
  <div class="space-y-4">
    <div>
      <h1 class="text-xl font-bold text-slate-800">副資材チェック</h1>
      <p class="text-sm text-slate-500">
        指示書（工程表）と実物タグの画像をアップロードすると、AIがレイアウト・順番・内容の3要素を
        規定・商品マスタの付属情報と突き合わせてチェックします。
      </p>
    </div>

    <!-- ページ内タブ（?tab= 同期。inventory.vue のタブパターン） -->
    <nav class="border-b border-slate-200" aria-label="副資材チェックのビュー切替">
      <ul class="scrollbar-hide -mb-px flex gap-1 overflow-x-auto">
        <li v-for="tab in TABS" :key="tab.key" class="shrink-0">
          <button
            type="button"
            class="flex items-center gap-1.5 whitespace-nowrap border-b-2 px-3 py-2.5 text-sm font-medium transition-colors"
            :class="
              activeTab === tab.key
                ? 'border-indigo-600 text-indigo-700'
                : 'border-transparent text-slate-500 hover:border-slate-300 hover:text-slate-800'
            "
            :aria-current="activeTab === tab.key ? 'page' : undefined"
            @click="setTab(tab.key)"
          >
            <component :is="tab.icon" class="h-4 w-4 shrink-0" />
            {{ tab.label }}
          </button>
        </li>
      </ul>
    </nav>

    <!-- ============ チェック履歴 ============ -->
    <section v-if="activeTab === 'history'" aria-label="チェック履歴" class="space-y-3">
      <!--
        AI 実行の非同期化により「一覧に processing 行が並ぶ」ことが常態になったが、一覧は
        タブ単位の遅延ロードで1回しか取得しない。一覧に留まる利用者が完了を確認できるよう
        手動更新の導線を置く（自動ポーリングは詳細ページが担う。一覧の全件ポーリングは無駄が大きい）。
        StatusBlock の外に置き、読込中・0件でも押せる位置を保つ。
      -->
      <div class="flex justify-end">
        <button
          type="button"
          class="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-xs font-medium text-slate-600 transition-colors hover:border-indigo-300 hover:text-indigo-700 disabled:cursor-not-allowed disabled:opacity-40"
          :disabled="historyLoading"
          @click="loadHistory"
        >
          <RefreshCw
            class="h-3.5 w-3.5 shrink-0"
            :class="historyLoading ? 'animate-spin' : ''"
            aria-hidden="true"
          />
          {{ historyLoading ? '更新中...' : '更新' }}
        </button>
      </div>

      <!--
        loading は「初回未取得（マウント直後の取得開始前・取得中）」だけにする
        （空状態の一瞬表示は防ぎつつ、更新・ページ送りの読み込み中は既存行を残す）。
        再取得中であることはボタン側のスピナで示す。
        なお再取得が失敗した場合は historyError により行がエラー表示へ置き換わる（従来と同じ）。
      -->
      <StatusBlock
        :loading="!historyLoaded && !historyError"
        :error="historyError"
        :empty="(history?.items.length ?? 0) === 0"
        empty-message="チェック履歴がありません。「新規チェック」タブから実行できます。"
      >
        <div class="space-y-3">
          <p class="text-xs text-slate-400">{{ historyPageInfo }}</p>

          <!-- PC はテーブル・モバイル(<768px)はカード（DataTable が md: ブレークポイントで切替） -->
          <DataTable
            :columns="historyColumns"
            :rows="history?.items ?? []"
            :row-key="(row: SubsidiaryCheckSummary) => row.checkId"
            clickable
            @row-click="handleHistoryRowClick"
          >
            <template #productLabel="{ row }">
              <span
                class="font-bold"
                :class="(row as SubsidiaryCheckSummary).productLabel ? 'text-slate-800' : 'font-normal text-slate-400'"
              >
                {{ (row as SubsidiaryCheckSummary).productLabel || '（商品未選択）' }}
              </span>
            </template>
            <template #status="{ row }">
              <span
                class="inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-xs font-medium"
                :class="SUBSIDIARY_CHECK_STATUSES[(row as SubsidiaryCheckSummary).status].className"
              >
                <component
                  :is="SUBSIDIARY_CHECK_STATUSES[(row as SubsidiaryCheckSummary).status].icon"
                  class="h-3 w-3"
                  :class="(row as SubsidiaryCheckSummary).status === 'processing' ? 'animate-spin' : ''"
                  aria-hidden="true"
                />
                {{ SUBSIDIARY_CHECK_STATUSES[(row as SubsidiaryCheckSummary).status].label }}
              </span>
            </template>
            <template #judgment="{ row }">
              <span
                v-if="(row as SubsidiaryCheckSummary).judgment"
                class="inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-xs font-medium"
                :class="SUBSIDIARY_CHECK_JUDGMENTS[(row as SubsidiaryCheckSummary).judgment!].className"
              >
                <component
                  :is="SUBSIDIARY_CHECK_JUDGMENTS[(row as SubsidiaryCheckSummary).judgment!].icon"
                  class="h-3 w-3"
                  aria-hidden="true"
                />
                {{ SUBSIDIARY_CHECK_JUDGMENTS[(row as SubsidiaryCheckSummary).judgment!].label }}
              </span>
              <span v-else class="text-xs text-slate-300">—</span>
            </template>
          </DataTable>

          <div
            v-if="(history?.totalCount ?? 0) > PAGE_SIZE"
            class="flex items-center justify-center gap-3"
          >
            <button
              type="button"
              class="rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-xs font-medium text-slate-600 transition-colors hover:border-indigo-300 disabled:cursor-not-allowed disabled:opacity-40"
              :disabled="historyPage <= 1 || historyLoading"
              @click="moveHistoryPage(-1)"
            >
              ← 前へ
            </button>
            <span class="text-xs text-slate-500">{{ historyPageInfo }}</span>
            <button
              type="button"
              class="rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-xs font-medium text-slate-600 transition-colors hover:border-indigo-300 disabled:cursor-not-allowed disabled:opacity-40"
              :disabled="!hasNextHistoryPage || historyLoading"
              @click="moveHistoryPage(1)"
            >
              次へ →
            </button>
          </div>

          <p class="text-xs text-slate-400">
            行をクリックすると指摘の詳細・画像を確認できます。AI判定は補助であり、最終判断は担当者が行ってください。
          </p>
        </div>
      </StatusBlock>
    </section>

    <!-- ============ 新規チェック ============ -->
    <section v-else-if="activeTab === 'new'" aria-label="新規チェック" class="space-y-4">
      <!-- 商品マスタ検索（任意） -->
      <div class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
        <h2 class="text-sm font-semibold text-slate-700">対象商品（任意）</h2>
        <p class="mt-0.5 text-xs text-slate-500">
          商品マスタから選択すると、付属情報（組成・原産国・表示順序等）を照合してチェック精度が上がります。
        </p>

        <div v-if="selectedProduct" class="mt-3">
          <span class="inline-flex items-center gap-1.5 rounded-full bg-indigo-50 px-3 py-1.5 text-sm font-medium text-indigo-700">
            {{ selectedProduct.label }}
            <button
              type="button"
              class="text-indigo-500 hover:text-indigo-900"
              aria-label="商品の選択を解除"
              :disabled="submitting"
              @click="clearProduct"
            >
              <X class="h-3.5 w-3.5" />
            </button>
          </span>
        </div>

        <template v-else>
          <form class="mt-3 flex flex-wrap items-center gap-2" @submit.prevent="searchProducts">
            <input
              v-model="productKeyword"
              type="search"
              placeholder="商品名・記号・品番・ブランドで検索"
              class="w-full max-w-xs rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-700 placeholder:text-slate-400 focus:border-indigo-400 focus:outline-none"
            >
            <button
              type="submit"
              class="inline-flex shrink-0 items-center gap-1.5 rounded-lg border border-slate-300 px-3 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50 disabled:opacity-50"
              :disabled="productSearchLoading"
            >
              <LoaderCircle v-if="productSearchLoading" class="h-4 w-4 animate-spin" />
              <Search v-else class="h-4 w-4" />
              検索
            </button>
          </form>

          <p
            v-if="productSearchError"
            class="mt-2 rounded-lg bg-red-50 px-3 py-2 text-xs text-red-700"
          >
            商品マスタの検索に失敗しました: {{ productSearchError }}
          </p>

          <div v-if="productResults" class="mt-3">
            <p
              v-if="productResults.items.length === 0 && productSearched"
              class="text-xs text-slate-400"
            >
              該当する商品が見つかりません。キーワードを変えてお試しください（商品未選択のままでも実行できます）。
            </p>
            <ul v-else class="divide-y divide-slate-100 rounded-lg border border-slate-200">
              <li v-for="product in productResults.items" :key="product.productId">
                <button
                  type="button"
                  class="flex w-full flex-wrap items-center gap-x-3 gap-y-0.5 px-3 py-2 text-left transition-colors hover:bg-indigo-50"
                  @click="selectProduct(product)"
                >
                  <span class="font-mono text-xs text-slate-500">{{ product.productTypeCrd }}</span>
                  <span class="min-w-0 flex-1 truncate text-sm font-medium text-slate-800">
                    {{ product.productName }}
                  </span>
                  <span v-if="product.brand" class="rounded-full bg-indigo-50 px-2 py-0.5 text-[11px] text-indigo-700">
                    {{ product.brand }}
                  </span>
                </button>
              </li>
            </ul>
            <p
              v-if="productResults.totalCount > productResults.items.length"
              class="mt-1 text-[11px] text-slate-400"
            >
              全 {{ formatNumber(productResults.totalCount) }} 件中 {{ formatNumber(productResults.items.length) }} 件を表示しています。キーワードで絞り込んでください。
            </p>
          </div>
        </template>
      </div>

      <!-- 画像アップロード（指示書 / タグ） -->
      <div class="grid grid-cols-1 gap-4 lg:grid-cols-2">
        <div
          v-for="upload in uploadAreas"
          :key="upload.kind"
          class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm"
        >
          <div class="flex flex-wrap items-baseline justify-between gap-2">
            <h2 class="text-sm font-semibold text-slate-700">{{ upload.title }}</h2>
            <span class="text-xs text-slate-400">
              {{ formatNumber(upload.images.length) }} / {{ maxCountOf(upload.kind) }} 枚
            </span>
          </div>
          <p class="mt-0.5 text-xs text-slate-500">
            {{ upload.note }}。JPEG / PNG・各 {{ formatFileSize(SUBSIDIARY_IMAGE_MAX_BYTES) }} 以下・全画像の合計 {{ formatFileSize(SUBSIDIARY_TOTAL_IMAGE_MAX_BYTES) }} 以内。
          </p>

          <input
            type="file"
            :accept="SUBSIDIARY_IMAGE_ACCEPT"
            multiple
            class="mt-3 w-full text-sm text-slate-600 file:mr-3 file:rounded-lg file:border-0 file:bg-slate-100 file:px-3 file:py-2 file:text-sm file:font-medium"
            :disabled="submitting"
            :aria-label="`${upload.title}を選択`"
            @change="onFileChange(upload.kind, $event)"
          >

          <!-- サムネイルプレビュー（個別削除可） -->
          <ul v-if="upload.images.length > 0" class="mt-3 grid grid-cols-3 gap-2 sm:grid-cols-4">
            <li
              v-for="(image, index) in upload.images"
              :key="image.previewUrl"
              class="group relative overflow-hidden rounded-lg border border-slate-200 bg-slate-50"
            >
              <img
                :src="image.previewUrl"
                :alt="`${upload.title} ${index + 1}: ${image.file.name}`"
                class="aspect-square w-full object-cover"
              >
              <button
                type="button"
                class="absolute right-1 top-1 rounded-md bg-white/90 p-1.5 text-slate-500 shadow-sm transition-colors hover:bg-rose-50 hover:text-rose-600"
                :aria-label="`${image.file.name} を削除`"
                :disabled="submitting"
                @click="removeImage(upload.kind, index)"
              >
                <Trash2 class="h-3.5 w-3.5" />
              </button>
              <p class="truncate bg-white px-1.5 py-1 text-[10px] text-slate-500" :title="image.file.name">
                {{ image.file.name }}
              </p>
            </li>
          </ul>
        </div>
      </div>

      <!-- ファイル選択時の検証エラー -->
      <ul
        v-if="fileErrors.length > 0"
        class="list-disc rounded-lg border border-amber-200 bg-amber-50 py-2 pl-8 pr-3 text-xs text-amber-700"
        role="alert"
      >
        <li v-for="(message, index) in fileErrors" :key="index">{{ message }}</li>
      </ul>

      <!-- 実行前のクライアント検証エラー -->
      <p
        v-if="formError"
        class="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700"
        role="alert"
      >
        {{ formError }}
      </p>

      <!-- サーバエラー（imports.vue と同様の ApiError パネル） -->
      <div v-if="submitError" class="rounded-lg bg-red-50 p-3 text-sm text-red-700">
        <div class="flex items-start gap-2">
          <CircleX class="h-5 w-5 shrink-0" />
          <div>
            <p class="font-medium">
              [{{ submitError.errorCode }}] {{ submitError.summary }}
            </p>
            <p v-if="submitError.remedy" class="text-xs text-red-500">
              {{ submitError.remedy }}
            </p>
          </div>
        </div>
        <ul
          v-if="submitError.details && submitError.details.length > 0"
          class="mt-2 max-h-40 list-disc overflow-y-auto pl-8 text-xs"
        >
          <li v-for="(detail, index) in submitError.details" :key="index">
            {{ detail }}
          </li>
        </ul>
      </div>

      <!-- 実行ボタン -->
      <div class="flex flex-wrap items-center gap-3">
        <button
          type="button"
          class="inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700 disabled:opacity-50"
          :disabled="!canSubmit"
          @click="runCheck"
        >
          <LoaderCircle v-if="submitting" class="h-4 w-4 animate-spin" />
          <Sparkles v-else class="h-4 w-4" />
          {{ submitting ? '登録中…' : 'AIチェックを実行' }}
        </button>
        <p v-if="!submitting" class="text-xs text-slate-400">
          実行すると画像が保存され、すぐに結果詳細へ移動します（AIチェックはバックグラウンドで実行され、結果は自動で表示されます）。
        </p>
      </div>
    </section>

    <!-- ============ ルールブック ============ -->
    <section v-else aria-label="ルールブック">
      <!-- loading は「未取得（タブ切替直後の取得開始前）」も含める（空状態の一瞬表示を防ぐ）。 -->
      <StatusBlock
        :loading="rulesLoading || (!rulesLoaded && !rulesError)"
        :error="rulesError"
        :empty="(ruleBook?.sections.length ?? 0) === 0"
        empty-message="表示できるルールがありません。"
      >
        <div class="space-y-4">
          <section
            v-for="section in ruleBook?.sections ?? []"
            :key="section.id"
            class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm"
            :aria-label="section.title"
          >
            <h2 class="text-sm font-bold text-slate-800">{{ section.title }}</h2>
            <p v-if="section.description" class="mt-0.5 text-xs text-slate-500">
              {{ section.description }}
            </p>
            <!-- PC はテーブル・モバイルはカード（DataTable の md: 切替） -->
            <div class="mt-3">
              <DataTable
                :columns="ruleColumns"
                :rows="section.items"
                :row-key="(row: SubsidiaryRuleItem) => row.name"
              />
            </div>
          </section>

          <p v-if="ruleBook" class="text-xs text-slate-400">
            出典: {{ ruleBook.source }}
          </p>
        </div>
      </StatusBlock>
    </section>
  </div>
</template>
