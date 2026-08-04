<script setup lang="ts">
import {
  ArrowLeft,
  ArrowRight,
  CircleX,
  ImageOff,
  LoaderCircle,
  RotateCcw,
} from 'lucide-vue-next'
import type {
  CareIcon,
  CareIconCatalogResponse,
  ManualCheckDetail,
  ManualCheckSummary,
  SubsidiaryCheckImageInfo,
} from '~/types/api'

/**
 * 手入力チェックの結果詳細。
 * 判定バナー（pass/warn/fail）・項目単位の突合結果（手入力 vs タグ読取）・
 * 手入力スナップショット・アップロード画像を表示する。
 * AI 抽出・突合はバックグラウンド実行のため、processing の間は自動ポーリングし、
 * completed / failed になった時点で表示を切り替える（既存の副資材チェック結果詳細と同じ方式）。
 */
useHead({ title: '手入力チェック結果 | UndeuxSales' })

const route = useRoute()
const router = useRouter()
const { get, post } = useApi()
const { getIdToken } = useAuth()
const config = useRuntimeConfig()
const apiBaseUrl = config.public.apiBaseUrl

const checkId = computed(() => String(route.params.checkId ?? ''))

const POLL_INTERVAL_MS = 5000
const POLL_MAX_CONSECUTIVE_FAILURES = 5
const POLL_MAX_ATTEMPTS = SUBSIDIARY_PROCESSING_STALE_MS / POLL_INTERVAL_MS

// ---- 詳細の取得 ----

const detail = ref<ManualCheckDetail | null>(null)
const loading = ref(true)
const errorMessage = ref<string | null>(null)
const notFound = ref(false)
const pollStoppedMessage = ref<string | null>(null)
let requestSeq = 0
let loadingSeq = 0

function isNotFoundError(error: unknown): boolean {
  const e = error as { statusCode?: number; status?: number; response?: { status?: number } } | null
  const status = e?.statusCode ?? e?.status ?? e?.response?.status ?? null
  return status === 404
}

async function load(): Promise<void> {
  const loadSeq = ++loadingSeq
  const seq = ++requestSeq
  loading.value = true
  errorMessage.value = null
  notFound.value = false
  try {
    const result = await get<ManualCheckDetail>(`/api/subsidiary-check/manual/${checkId.value}`)
    if (seq !== requestSeq) return
    detail.value = result
    if (result.summary.status === 'processing') startPolling()
    void loadImages(result)
  } catch (error) {
    if (loadSeq !== loadingSeq) return
    if (isNotFoundError(error)) {
      notFound.value = true
      detail.value = null
    } else {
      console.error('[manual-check] チェック結果の取得に失敗しました:', error)
      errorMessage.value = apiErrorMessage(error)
    }
  } finally {
    if (loadSeq === loadingSeq) {
      loading.value = false
    }
  }
}

// ---- processing 中の自動ポーリング ----

let pollGeneration = 0
let polling = false
let unmounted = false

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms))
}

function stopPolling(): void {
  pollGeneration += 1
  polling = false
}

function startPolling(): void {
  if (unmounted || polling) return
  pollStoppedMessage.value = null
  polling = true
  void runPolling(++pollGeneration)
}

async function runPolling(generation: number): Promise<void> {
  let failures = 0
  for (let attempt = 0; attempt < POLL_MAX_ATTEMPTS; attempt++) {
    await sleep(POLL_INTERVAL_MS)
    if (generation !== pollGeneration) return

    const seq = ++requestSeq
    try {
      const result = await get<ManualCheckDetail>(`/api/subsidiary-check/manual/${checkId.value}`)
      if (generation !== pollGeneration) return
      failures = 0
      if (seq !== requestSeq) continue
      detail.value = result
      errorMessage.value = null
    } catch (error) {
      if (generation !== pollGeneration) return
      failures += 1
      console.warn(
        `[manual-check] 処理状況の自動更新に失敗しました（${failures}/${POLL_MAX_CONSECUTIVE_FAILURES}）:`,
        error,
      )
      if (failures >= POLL_MAX_CONSECUTIVE_FAILURES) {
        pollStoppedMessage.value = '処理状況の自動更新に繰り返し失敗しました。再読込してください。'
        stopPolling()
        return
      }
    }
  }
  if (generation === pollGeneration) {
    pollStoppedMessage.value =
      '処理状況の自動更新を停止しました（一定時間が経過したため）。'
      + (isStaleProcessing.value
        ? '完了していない場合は再読込するか、下のボタンから再実行してください。'
        : '完了していない場合は再読込してください。')
    stopPolling()
  }
}

watch(
  () => detail.value?.summary.status ?? null,
  (status) => {
    if (status === 'processing') startPolling()
    else stopPolling()
  },
)

// ---- 洗濯アイコンマスタ（careIcon 項目の表示用。非ブロッキング） ----

const careIcons = ref<Map<string, CareIcon>>(new Map())

async function loadCareIcons(): Promise<void> {
  if (careIcons.value.size > 0) return
  try {
    const catalog = await get<CareIconCatalogResponse>('/api/subsidiary-check/manual/care-icons')
    const map = new Map<string, CareIcon>()
    for (const icon of catalog.icons) map.set(icon.code, icon)
    careIcons.value = map
  } catch (error) {
    // アイコン表示は補助のため、失敗しても結果表示は妨げない（原則4。コード/ラベルのテキストで代替）。
    console.warn('[manual-check] 洗濯アイコンマスタの取得に失敗しました:', error)
  }
}

// ---- 画像ギャラリー（認証付き fetch + objectURL） ----

interface GalleryImage {
  info: SubsidiaryCheckImageInfo
  objectUrl: string | null
  failed: boolean
}

const galleryImages = ref<GalleryImage[]>([])
const imagesLoading = ref(false)
let imagesRequestSeq = 0

function revokeGallery(): void {
  for (const image of galleryImages.value) {
    if (image.objectUrl) URL.revokeObjectURL(image.objectUrl)
  }
  galleryImages.value = []
}

async function loadImages(target: ManualCheckDetail): Promise<void> {
  if (target.images.length === 0) {
    imagesRequestSeq += 1
    revokeGallery()
    imagesLoading.value = false
    return
  }
  const seq = ++imagesRequestSeq
  revokeGallery()
  imagesLoading.value = true
  try {
    const token = await getIdToken()
    const results = await mapWithConcurrencyLimit(
      target.images,
      SUBSIDIARY_GALLERY_FETCH_CONCURRENCY,
      async (info): Promise<GalleryImage> => {
        try {
          const response = await fetch(
            `${apiBaseUrl}/api/subsidiary-check/manual/${target.summary.checkId}/images/${info.imageId}`,
            { headers: token ? { Authorization: `Bearer ${token}` } : undefined },
          )
          if (!response.ok) return { info, objectUrl: null, failed: true }
          const blob = await response.blob()
          return { info, objectUrl: URL.createObjectURL(blob), failed: false }
        } catch {
          return { info, objectUrl: null, failed: true }
        }
      },
    )
    if (seq !== imagesRequestSeq) {
      for (const result of results) {
        if (result.objectUrl) URL.revokeObjectURL(result.objectUrl)
      }
      return
    }
    galleryImages.value = results
  } catch {
    if (seq === imagesRequestSeq) {
      galleryImages.value = target.images.map((info) => ({ info, objectUrl: null, failed: true }))
    }
  } finally {
    if (seq === imagesRequestSeq) {
      imagesLoading.value = false
    }
  }
}

const hasFailedImages = computed(() => galleryImages.value.some((image) => image.failed))

function retryImages(): void {
  if (!detail.value) return
  void loadImages(detail.value)
}

onBeforeUnmount(() => {
  unmounted = true
  stopPolling()
  requestSeq += 1
  imagesRequestSeq += 1
  revokeGallery()
})

// ---- 突合結果・派生値 ----

const summary = computed(() => detail.value?.summary ?? null)

/** careIcon 項目のキー → 手入力アイコンコード列（入力スナップショットから引く。結果にアイコンを描く用）。 */
const careIconCodesByKey = computed(() => {
  const map = new Map<string, string[]>()
  for (const field of detail.value?.input.fields ?? []) {
    if (field.compare === 'careIcon') map.set(field.key, field.values)
  }
  return map
})

function iconsFor(codes: string[]): CareIcon[] {
  return codes.map((code) => careIcons.value.get(code)).filter((i): i is CareIcon => i != null)
}

const metaText = computed(() => {
  const s = summary.value
  if (!s) return ''
  const parts = [`実行 ${formatDateTime(s.createdAt)}`]
  if (s.checkedAt) parts.push(`完了 ${formatDateTime(s.checkedAt)}`)
  if (s.patternName) parts.push(`パターン ${s.patternName}`)
  if (s.aiModel) parts.push(`モデル ${s.aiModel}`)
  if (s.createdBy) parts.push(`実行者 ${s.createdBy}`)
  return parts.join(' ・ ')
})

// ---- 再実行 ----

const rerunning = ref(false)
const rerunError = ref<string | null>(null)

const isStaleProcessing = computed(() => {
  const s = detail.value?.summary
  if (!s || s.status !== 'processing') return false
  return Date.now() - new Date(s.startedAt ?? s.createdAt).getTime() >= SUBSIDIARY_PROCESSING_STALE_MS
})

async function rerun(): Promise<void> {
  if (rerunning.value) return
  rerunning.value = true
  rerunError.value = null
  const seq = ++requestSeq
  try {
    const updated = await post<ManualCheckDetail>(`/api/subsidiary-check/manual/${checkId.value}/rerun`, {})
    if (seq !== requestSeq) return
    detail.value = updated
    startPolling()
    if (galleryImages.value.some((image) => image.failed)) {
      void loadImages(updated)
    }
  } catch (error) {
    console.error('[manual-check] 再実行に失敗しました:', error)
    rerunError.value = apiErrorMessage(error)
  } finally {
    rerunning.value = false
  }
}

function goBack(): void {
  router.back()
}

watch(checkId, () => {
  stopPolling()
  imagesRequestSeq += 1
  revokeGallery()
  void load()
})

onMounted(() => {
  void load()
  void loadCareIcons()
})

function findingsSummaryText(s: ManualCheckSummary): string {
  const parts: string[] = []
  if (s.mismatchCount > 0) parts.push(`相違 ${formatNumber(s.mismatchCount)}`)
  if (s.warnCount > 0) parts.push(`要確認 ${formatNumber(s.warnCount)}`)
  if (s.matchCount > 0) parts.push(`一致 ${formatNumber(s.matchCount)}`)
  return parts.length > 0 ? parts.join(' ・ ') : '突合対象なし'
}
</script>

<template>
  <div class="space-y-4">
    <div class="flex flex-wrap items-center gap-2">
      <button
        type="button"
        class="inline-flex min-h-[36px] items-center gap-1 rounded-lg border border-slate-300 px-3 py-2 text-xs text-slate-600 hover:bg-slate-50"
        @click="goBack"
      >
        <ArrowLeft class="h-3.5 w-3.5" />
        戻る
      </button>
      <NuxtLink
        to="/subsidiary-check?tab=manual"
        class="inline-flex min-h-[36px] items-center gap-1 rounded-lg border border-slate-300 px-3 py-2 text-xs text-slate-600 hover:bg-slate-50"
      >
        手入力チェックへ
      </NuxtLink>
      <button
        v-if="errorMessage && !notFound"
        type="button"
        class="inline-flex min-h-[36px] items-center gap-1 rounded-lg border border-slate-300 px-3 py-2 text-xs text-slate-600 hover:bg-slate-50 disabled:opacity-50"
        :disabled="loading"
        @click="load"
      >
        <RotateCcw class="h-3.5 w-3.5" :class="loading ? 'animate-spin' : ''" />
        再読込
      </button>
    </div>

    <div
      v-if="notFound"
      class="rounded-xl border border-amber-200 bg-amber-50 p-4 text-sm text-amber-700"
    >
      指定されたチェック結果が見つかりません。URL を確認するか、手入力チェックの履歴からお選びください。
    </div>

    <StatusBlock v-if="!notFound" :loading="loading" :error="errorMessage">
      <div v-if="detail && summary" class="space-y-4">
        <div>
          <h1 class="text-xl font-bold text-slate-800">手入力チェック結果</h1>
          <p class="text-sm text-slate-500">{{ summary.productLabel || '（商品ラベル未設定）' }}</p>
        </div>

        <!-- 失敗パネル -->
        <div
          v-if="summary.status === 'failed'"
          class="rounded-xl border border-rose-200 bg-rose-50 p-4"
          role="alert"
        >
          <div class="flex items-start gap-2 text-sm text-rose-700">
            <CircleX class="h-5 w-5 shrink-0" aria-hidden="true" />
            <div class="min-w-0 flex-1">
              <p class="font-bold">タグ読取・突合の実行に失敗しました</p>
              <p v-if="summary.errorMessage" class="mt-1 break-words text-xs text-rose-600">
                {{ summary.errorMessage }}
              </p>
              <p class="mt-1 text-xs text-rose-500">
                入力・画像は保存されています。再実行で同じ内容に対して突合をやり直せます。
              </p>
            </div>
          </div>
          <div class="mt-3 flex flex-wrap items-center gap-3">
            <button
              type="button"
              class="inline-flex items-center gap-1.5 rounded-lg bg-rose-600 px-4 py-2 text-sm font-medium text-white hover:bg-rose-700 disabled:opacity-50"
              :disabled="rerunning"
              @click="rerun"
            >
              <LoaderCircle v-if="rerunning" class="h-4 w-4 animate-spin" />
              <RotateCcw v-else class="h-4 w-4" />
              {{ rerunning ? '再実行を登録中…' : '再実行' }}
            </button>
            <p v-if="rerunError" class="text-xs text-rose-700" role="alert">再実行に失敗しました: {{ rerunError }}</p>
          </div>
        </div>

        <!-- 処理中パネル -->
        <div
          v-else-if="summary.status === 'processing'"
          class="rounded-xl border border-sky-200 bg-sky-50 p-4 text-sm text-sky-700"
          role="status"
        >
          <div class="flex flex-wrap items-center gap-3">
            <LoaderCircle class="h-5 w-5 shrink-0" :class="pollStoppedMessage ? '' : 'animate-spin'" aria-hidden="true" />
            <span v-if="pollStoppedMessage" class="flex-1 font-medium">{{ pollStoppedMessage }}</span>
            <span v-else class="flex-1">
              タグ画像を読み取って突合を処理中です…（数十秒かかることがあります）。完了すると自動で表示が切り替わります。
            </span>
            <button
              type="button"
              class="inline-flex min-h-[36px] items-center gap-1 rounded-lg border border-sky-300 bg-white px-3 py-2 text-xs text-sky-700 hover:bg-sky-100"
              @click="load"
            >
              <RotateCcw class="h-3.5 w-3.5" />
              再読込
            </button>
          </div>
          <div v-if="isStaleProcessing" class="mt-3 border-t border-sky-200 pt-3">
            <p class="text-xs text-sky-600">
              処理中のまま時間が経過しています。中断された可能性があるため、再実行できます。
            </p>
            <div class="mt-2 flex flex-wrap items-center gap-3">
              <button
                type="button"
                class="inline-flex items-center gap-1.5 rounded-lg bg-sky-600 px-4 py-2 text-sm font-medium text-white hover:bg-sky-700 disabled:opacity-50"
                :disabled="rerunning"
                @click="rerun"
              >
                <LoaderCircle v-if="rerunning" class="h-4 w-4 animate-spin" />
                <RotateCcw v-else class="h-4 w-4" />
                {{ rerunning ? '再実行を登録中…' : '再実行' }}
              </button>
              <p v-if="rerunError" class="text-xs text-rose-700" role="alert">再実行に失敗しました: {{ rerunError }}</p>
            </div>
          </div>
        </div>

        <!-- 判定バナー -->
        <div
          v-else-if="summary.judgment"
          class="rounded-xl border p-4"
          :class="subsidiaryCheckJudgmentPresentation(summary.judgment).bannerClass"
        >
          <div class="flex flex-wrap items-center gap-3">
            <component
              :is="subsidiaryCheckJudgmentPresentation(summary.judgment).icon"
              class="h-8 w-8 shrink-0"
              aria-hidden="true"
            />
            <div class="min-w-0 flex-1">
              <p class="text-lg font-bold">{{ subsidiaryCheckJudgmentPresentation(summary.judgment).label }}</p>
              <p class="text-xs opacity-80">
                {{ findingsSummaryText(summary) }}（全 {{ formatNumber(summary.fieldCount) }} 項目）
              </p>
            </div>
          </div>
        </div>

        <p v-if="metaText" class="text-xs text-slate-400">{{ metaText }}</p>

        <!-- 項目別 突合結果 -->
        <div
          v-if="detail.results.length > 0"
          class="rounded-xl border border-slate-200 bg-white shadow-sm"
        >
          <div class="border-b border-slate-100 px-4 py-3">
            <h2 class="text-sm font-bold text-slate-800">項目別の突合結果</h2>
            <p class="mt-0.5 text-xs text-slate-400">
              手入力（正）とタグ画像の読取値を項目ごとに突き合わせた結果です。AI判定は補助であり、最終判断は担当者が行ってください。
            </p>
          </div>
          <ul class="divide-y divide-slate-100">
            <li v-for="result in detail.results" :key="result.key" class="p-4">
              <div class="flex flex-wrap items-center gap-2">
                <span
                  class="inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-xs font-medium"
                  :class="manualCheckVerdictPresentation(result.verdict).className"
                >
                  <component
                    :is="manualCheckVerdictPresentation(result.verdict).icon"
                    class="h-3 w-3"
                    aria-hidden="true"
                  />
                  {{ manualCheckVerdictPresentation(result.verdict).label }}
                </span>
                <h3 class="min-w-0 flex-1 text-sm font-semibold text-slate-800">{{ result.label }}</h3>
                <span class="rounded bg-slate-100 px-1.5 py-0.5 text-[10px] text-slate-500">
                  {{ manualCompareModeLabel(result.compare) }}
                </span>
              </div>

              <!-- 手入力 vs タグ読取 -->
              <div class="mt-2 grid grid-cols-1 gap-2 sm:grid-cols-[1fr_auto_1fr] sm:items-center">
                <div class="rounded-lg bg-slate-50 px-3 py-2">
                  <p class="text-[10px] font-medium text-slate-400">手入力</p>
                  <!-- careIcon はアイコン列も描く（マスタ取得済みのとき） -->
                  <div
                    v-if="result.compare === 'careIcon' && iconsFor(careIconCodesByKey.get(result.key) ?? []).length > 0"
                    class="mt-1 flex flex-wrap items-center gap-1"
                  >
                    <span
                      v-for="(icon, i) in iconsFor(careIconCodesByKey.get(result.key) ?? [])"
                      :key="`${icon.code}-${i}`"
                      class="grid h-7 w-7 place-items-center text-slate-700"
                      :title="icon.label"
                      v-html="icon.svg"
                    />
                  </div>
                  <p class="mt-0.5 whitespace-pre-wrap break-words text-sm text-slate-700">
                    {{ result.expected || '—' }}
                  </p>
                </div>
                <ArrowRight class="mx-auto hidden h-4 w-4 text-slate-300 sm:block" aria-hidden="true" />
                <div class="rounded-lg bg-slate-50 px-3 py-2">
                  <p class="text-[10px] font-medium text-slate-400">タグ読取</p>
                  <p class="mt-0.5 whitespace-pre-wrap break-words text-sm text-slate-700">
                    {{ result.extracted || '（読取なし）' }}
                  </p>
                </div>
              </div>

              <p class="mt-2 whitespace-pre-wrap text-xs leading-relaxed text-slate-500">{{ result.detail }}</p>
            </li>
          </ul>
        </div>

        <!-- 画像ギャラリー -->
        <section
          v-if="detail.images.length > 0"
          class="rounded-xl border border-slate-200 bg-white shadow-sm"
          aria-label="アップロード画像"
        >
          <div class="flex flex-wrap items-start justify-between gap-2 border-b border-slate-100 px-4 py-3">
            <div>
              <h2 class="text-sm font-bold text-slate-800">タグ画像</h2>
              <p class="mt-0.5 text-xs text-slate-400">読取に使用したタグ画像です（目視確認の補助にお使いください）。</p>
            </div>
            <button
              v-if="hasFailedImages"
              type="button"
              class="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-xs font-medium text-slate-600 hover:border-indigo-300 hover:text-indigo-700 disabled:opacity-40"
              :disabled="imagesLoading"
              @click="retryImages"
            >
              <RotateCcw class="h-3.5 w-3.5" :class="imagesLoading ? 'animate-spin' : ''" />
              {{ imagesLoading ? '取得中...' : '画像を再取得' }}
            </button>
          </div>
          <div class="space-y-3 p-4">
            <div v-if="imagesLoading" class="flex items-center gap-2 text-xs text-slate-500">
              <LoaderCircle class="h-4 w-4 animate-spin text-indigo-500" /> 画像を読み込み中...
            </div>
            <ul class="grid grid-cols-2 gap-2 sm:grid-cols-3 lg:grid-cols-4">
              <li
                v-for="image in galleryImages"
                :key="image.info.imageId"
                class="overflow-hidden rounded-lg border border-slate-200 bg-slate-50"
              >
                <img
                  v-if="image.objectUrl"
                  :src="image.objectUrl"
                  :alt="`タグ画像: ${image.info.fileName}`"
                  class="aspect-square w-full bg-white object-contain"
                >
                <div
                  v-else
                  class="flex aspect-square w-full flex-col items-center justify-center gap-1 text-slate-400"
                >
                  <ImageOff class="h-6 w-6" aria-hidden="true" />
                  <span class="text-[10px]">{{ image.failed ? '取得に失敗しました' : '読み込み中…' }}</span>
                </div>
                <p class="truncate bg-white px-1.5 py-1 text-[10px] text-slate-500" :title="image.info.fileName">
                  {{ image.info.fileName }}（{{ formatFileSize(image.info.sizeBytes) }}）
                </p>
              </li>
            </ul>
          </div>
        </section>
      </div>
    </StatusBlock>
  </div>
</template>
