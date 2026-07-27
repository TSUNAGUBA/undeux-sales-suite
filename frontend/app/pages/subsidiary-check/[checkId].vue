<script setup lang="ts">
import {
  ArrowLeft,
  CircleX,
  ExternalLink,
  ImageOff,
  LoaderCircle,
  RotateCcw,
} from 'lucide-vue-next'
import type {
  SubsidiaryCheckDetail,
  SubsidiaryCheckImageInfo,
} from '~/types/api'

/**
 * 副資材チェックの結果詳細。
 * 判定バナー（pass/warn/fail）・カテゴリ別（レイアウト/順番/内容）の指摘・
 * 商品情報+付属情報・アップロード画像ギャラリーを表示する。
 * status=failed のチェック、および processing のまま一定時間経過した孤児チェックには
 * 再実行（手動回復パス）を提供する。
 *
 * AI 実行はサーバ側でバックグラウンド実行されるため（新規作成・再実行とも POST は
 * status=processing を即座に返す）、processing の間は本画面が詳細を自動ポーリングして
 * completed / failed になった時点で表示を切り替える（mart 再構築の status ポーリングと同じ方式）。
 *
 * 画像は Authorization ヘッダが必要なため <img src="API URL"> の直指定はできない。
 * RagKnowledgeSection.vue の原本ダウンロードと同じ「認証付き fetch + URL.createObjectURL」
 * 方式で取得し、objectURL は再取得時・アンマウント時に必ず revoke する。
 */
useHead({ title: '副資材チェック結果 | UndeuxSales' })

const route = useRoute()
const router = useRouter()
const { get, post } = useApi()
const { getIdToken } = useAuth()
const config = useRuntimeConfig()
const apiBaseUrl = config.public.apiBaseUrl

const checkId = computed(() => String(route.params.checkId ?? ''))

/**
 * processing の間に詳細を自動再取得する間隔（5秒）。
 * mart 再構築の status ポーリング（useMart.ts）と同じ間隔に揃える。
 * AI チェックは通常数十秒で完了するため、5秒間隔なら体感の遅れは小さく、
 * リクエスト数も1件あたり十数回に収まる。
 */
const POLL_INTERVAL_MS = 5000

// ---- 詳細の取得 ----

const detail = ref<SubsidiaryCheckDetail | null>(null)
const loading = ref(true)
const errorMessage = ref<string | null>(null)
const notFound = ref(false)
// 連続遷移で古い応答が後着しても表示を上書きしないためのリクエスト世代。
let requestSeq = 0

/** HTTP 404（チェックが見つからない）か。エラーコードはバックエンド採番のためステータスで判定する。 */
function isNotFoundError(error: unknown): boolean {
  const e = error as { statusCode?: number; status?: number; response?: { status?: number } } | null
  const status = e?.statusCode ?? e?.status ?? e?.response?.status ?? null
  return status === 404
}

async function load(): Promise<void> {
  const seq = ++requestSeq
  loading.value = true
  errorMessage.value = null
  notFound.value = false
  try {
    const result = await get<SubsidiaryCheckDetail>(`/api/subsidiary-check/${checkId.value}`)
    if (seq !== requestSeq) return
    detail.value = result
    // 画像取得は補助表示のため非ブロッキング（失敗しても指摘・判定は表示する。原則4）。
    void loadImages(result)
  } catch (error) {
    if (seq !== requestSeq) return
    if (isNotFoundError(error)) {
      notFound.value = true
      detail.value = null
    } else {
      console.error('[subsidiary-check] チェック結果の取得に失敗しました:', error)
      errorMessage.value = apiErrorMessage(error)
    }
  } finally {
    if (seq === requestSeq) {
      loading.value = false
    }
  }
}

// ---- processing 中の自動ポーリング ----

let pollTimer: ReturnType<typeof setInterval> | null = null

function stopPolling(): void {
  if (pollTimer !== null) {
    clearInterval(pollTimer)
    pollTimer = null
  }
}

function startPolling(): void {
  if (pollTimer !== null) return
  pollTimer = setInterval(() => {
    void pollDetail()
  }, POLL_INTERVAL_MS)
}

/**
 * ポーリングによる詳細の再取得。
 * ローディング表示・エラーパネルは切り替えず（画面がちらつかない・壊れない）、
 * 失敗しても次回の周期で再試行する（ネットワーク断等のグレースフルデグラデーション・原則4）。
 * 画像はチェック登録時に確定していて以降変化しないため、再取得しない。
 */
async function pollDetail(): Promise<void> {
  const seq = ++requestSeq
  try {
    const result = await get<SubsidiaryCheckDetail>(`/api/subsidiary-check/${checkId.value}`)
    if (seq !== requestSeq) return
    detail.value = result
  } catch (error) {
    console.warn('[subsidiary-check] 処理状況の自動更新に失敗しました（次回再試行します）:', error)
  }
}

// status が processing の間だけポーリングする（completed / failed になったら止める）。
watch(
  () => detail.value?.summary.status ?? null,
  (status) => {
    if (status === 'processing') startPolling()
    else stopPolling()
  },
)

// ページ離脱後に走り続けないよう、アンマウント時に必ず停止する。
onUnmounted(stopPolling)

// ---- 画像ギャラリー（認証付き fetch + objectURL） ----

interface GalleryImage {
  info: SubsidiaryCheckImageInfo
  /** 表示用 objectURL（取得失敗時は null）。revoke 責務は本ページが持つ。 */
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

async function loadImages(target: SubsidiaryCheckDetail): Promise<void> {
  const seq = ++imagesRequestSeq
  revokeGallery()
  if (target.images.length === 0) return
  imagesLoading.value = true
  try {
    const token = await getIdToken()
    // 1枚の取得失敗でギャラリー全体を止めない（失敗した枠のみプレースホルダ表示）。
    const results = await Promise.all(
      target.images.map(async (info): Promise<GalleryImage> => {
        try {
          const response = await fetch(
            `${apiBaseUrl}/api/subsidiary-check/${target.summary.checkId}/images/${info.imageId}`,
            { headers: token ? { Authorization: `Bearer ${token}` } : undefined },
          )
          if (!response.ok) return { info, objectUrl: null, failed: true }
          const blob = await response.blob()
          return { info, objectUrl: URL.createObjectURL(blob), failed: false }
        } catch {
          return { info, objectUrl: null, failed: true }
        }
      }),
    )
    if (seq !== imagesRequestSeq) {
      // 後着の旧リクエスト: 生成済み objectURL を破棄してリークさせない。
      for (const result of results) {
        if (result.objectUrl) URL.revokeObjectURL(result.objectUrl)
      }
      return
    }
    galleryImages.value = results
  } finally {
    if (seq === imagesRequestSeq) {
      imagesLoading.value = false
    }
  }
}

onBeforeUnmount(() => {
  // 進行中の取得が後着で objectURL を作っても seq ガードが revoke するため、表示済み分のみ破棄する。
  imagesRequestSeq += 1
  revokeGallery()
})

/** 種別（指示書 → タグ）ごとに sortOrder 昇順で並べたギャラリーグループ。 */
const galleryGroups = computed(() =>
  (['instruction', 'tag'] as const)
    .map((kind) => ({
      kind,
      label: SUBSIDIARY_CHECK_IMAGE_KINDS[kind].label,
      images: galleryImages.value
        .filter((image) => image.info.kind === kind)
        .sort((a, b) => a.info.sortOrder - b.info.sortOrder),
    }))
    .filter((group) => group.images.length > 0),
)

// ---- カテゴリ別の指摘（layout → order → content の3セクション） ----

const findingGroups = computed(() =>
  SUBSIDIARY_CHECK_CATEGORY_ORDER.map((category) => ({
    category,
    label: SUBSIDIARY_CHECK_CATEGORIES[category].label,
    description: SUBSIDIARY_CHECK_CATEGORIES[category].description,
    findings: (detail.value?.findings ?? []).filter((finding) => finding.category === category),
  })),
)

// ---- 再実行（failed / 長時間 processing の孤児。手動回復パス） ----

const rerunning = ref(false)
const rerunError = ref<string | null>(null)

/**
 * processing のまま SUBSIDIARY_PROCESSING_STALE_MS（バックエンド
 * SubsidiaryCheckService.ProcessingStaleAfter と同値）超経過した「孤児」か。
 * プロセスクラッシュ等で結果が確定しない場合の回復導線（rerun）を出す判定に使う。
 * 基準時刻は startedAt（最後に AI 実行を開始した日時）で、バックエンドの rerun クレーム条件と一致させる
 * （再実行中は startedAt が進むため、実行中のチェックに再実行導線は出ない）。
 * startedAt が未設定の旧データは createdAt で代替する（バックエンドの COALESCE と同じ扱い）。
 * 時刻は detail 取得時点で評価される（computed は detail 変更で再評価）。processing 中は
 * 自動ポーリングが detail を更新し続けるため、滞留超過になった時点で再実行導線が現れる。
 */
const isStaleProcessing = computed(() => {
  const s = detail.value?.summary
  if (!s || s.status !== 'processing') return false
  return Date.now() - new Date(s.startedAt ?? s.createdAt).getTime() >= SUBSIDIARY_PROCESSING_STALE_MS
})

/**
 * AI チェックの再実行を要求する。POST はクレーム（実行権の確保）だけを同期で行い
 * status=processing の詳細を即座に返すため、完了は自動ポーリングで追跡する
 * （detail が processing になることで status watcher がポーリングを開始する）。
 */
async function rerun(): Promise<void> {
  if (rerunning.value) return
  rerunning.value = true
  rerunError.value = null
  try {
    const updated = await post<SubsidiaryCheckDetail>(
      `/api/subsidiary-check/${checkId.value}/rerun`,
      {},
    )
    detail.value = updated
    // 画像自体は不変だが、初回取得に失敗していた場合の回復も兼ねて再取得する。
    void loadImages(updated)
  } catch (error) {
    console.error('[subsidiary-check] AIチェックの再実行に失敗しました:', error)
    rerunError.value = apiErrorMessage(error)
  } finally {
    rerunning.value = false
  }
}

// ---- 派生値・操作 ----

const summary = computed(() => detail.value?.summary ?? null)

/** バナー下のメタ情報（実行日時・完了日時・AIモデル）。 */
const metaText = computed(() => {
  const s = summary.value
  if (!s) return ''
  const parts = [`実行 ${formatDateTime(s.createdAt)}`]
  if (s.checkedAt) parts.push(`完了 ${formatDateTime(s.checkedAt)}`)
  if (s.aiModel) parts.push(`モデル ${s.aiModel}`)
  if (s.createdBy) parts.push(`実行者 ${s.createdBy}`)
  return parts.join(' ・ ')
})

function goBack(): void {
  router.back()
}

watch(checkId, () => {
  void load()
})

onMounted(() => {
  void load()
})
</script>

<template>
  <div class="space-y-4">
    <!-- 戻る・一覧導線 -->
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
        to="/subsidiary-check"
        class="inline-flex min-h-[36px] items-center gap-1 rounded-lg border border-slate-300 px-3 py-2 text-xs text-slate-600 hover:bg-slate-50"
      >
        チェック履歴一覧へ
      </NuxtLink>
    </div>

    <div
      v-if="notFound"
      class="rounded-xl border border-amber-200 bg-amber-50 p-4 text-sm text-amber-700"
    >
      指定されたチェック結果が見つかりません。URL を確認するか、チェック履歴一覧からお選びください。
    </div>

    <StatusBlock v-if="!notFound" :loading="loading" :error="errorMessage">
      <div v-if="detail && summary" class="space-y-4">
        <div>
          <h1 class="text-xl font-bold text-slate-800">副資材チェック結果</h1>
          <p class="text-sm text-slate-500">
            {{ summary.productLabel || '（商品未選択）' }}
          </p>
        </div>

        <!-- ============ 判定バナー / 失敗・処理中パネル ============ -->
        <div
          v-if="summary.status === 'failed'"
          class="rounded-xl border border-rose-200 bg-rose-50 p-4"
          role="alert"
        >
          <div class="flex items-start gap-2 text-sm text-rose-700">
            <CircleX class="h-5 w-5 shrink-0" aria-hidden="true" />
            <div class="min-w-0 flex-1">
              <p class="font-bold">AIチェックの実行に失敗しました</p>
              <p v-if="summary.errorMessage" class="mt-1 break-words text-xs text-rose-600">
                {{ summary.errorMessage }}
              </p>
              <p class="mt-1 text-xs text-rose-500">
                アップロード済みの画像は保存されています。再実行で同じ画像に対して AI チェックをやり直せます。
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
              {{ rerunning ? '再実行を登録中…' : 'AIチェックを再実行' }}
            </button>
            <p v-if="rerunError" class="text-xs text-rose-700" role="alert">
              再実行に失敗しました: {{ rerunError }}
            </p>
          </div>
        </div>

        <div
          v-else-if="summary.status === 'processing'"
          class="rounded-xl border border-sky-200 bg-sky-50 p-4 text-sm text-sky-700"
        >
          <div class="flex flex-wrap items-center gap-3">
            <LoaderCircle class="h-5 w-5 shrink-0 animate-spin" aria-hidden="true" />
            <span class="flex-1">
              AIチェックを処理中です…（数十秒かかることがあります）。完了すると自動で表示が切り替わります。
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
          <!-- 長時間 processing のままの孤児チェックには再実行導線を出す（バックエンドの孤児回復パス） -->
          <div v-if="isStaleProcessing" class="mt-3 border-t border-sky-200 pt-3">
            <p class="text-xs text-sky-600">
              処理中のまま時間が経過しています。処理が中断された可能性があるため、再実行できます
              （同じ画像に対して AI チェックをやり直します）。
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
                {{ rerunning ? '再実行を登録中…' : 'AIチェックを再実行' }}
              </button>
              <p v-if="rerunError" class="text-xs text-rose-700" role="alert">
                再実行に失敗しました: {{ rerunError }}
              </p>
            </div>
          </div>
        </div>

        <div
          v-else-if="summary.judgment"
          class="rounded-xl border p-4"
          :class="SUBSIDIARY_CHECK_JUDGMENTS[summary.judgment].bannerClass"
        >
          <div class="flex flex-wrap items-center gap-3">
            <component
              :is="SUBSIDIARY_CHECK_JUDGMENTS[summary.judgment].icon"
              class="h-8 w-8 shrink-0"
              aria-hidden="true"
            />
            <div class="min-w-0 flex-1">
              <p class="text-lg font-bold">
                {{ SUBSIDIARY_CHECK_JUDGMENTS[summary.judgment].label }}
              </p>
              <p class="text-xs opacity-80">
                <template v-if="summary.failCount > 0 || summary.warnCount > 0">
                  要修正 {{ formatNumber(summary.failCount) }} 件 ・
                  要確認 {{ formatNumber(summary.warnCount) }} 件
                  （全 {{ formatNumber(summary.findingCount) }} 件の確認結果）
                </template>
                <template v-else>
                  指摘はありません（全 {{ formatNumber(summary.findingCount) }} 件の確認結果）
                </template>
              </p>
            </div>
          </div>
        </div>

        <p v-if="metaText" class="text-xs text-slate-400">{{ metaText }}</p>

        <!-- ============ 商品情報 + 付属情報 ============ -->
        <div
          v-if="detail.product"
          class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm"
        >
          <div class="flex flex-wrap items-start justify-between gap-2">
            <div class="min-w-0">
              <h2 class="text-sm font-semibold text-slate-700">対象商品</h2>
              <p class="mt-1 text-sm font-bold text-slate-800">{{ detail.product.productName }}</p>
            </div>
            <NuxtLink
              :to="`/product-master/${detail.product.productId}`"
              class="inline-flex min-h-[36px] shrink-0 items-center gap-1 rounded-lg border border-slate-300 px-3 py-2 text-xs text-slate-600 hover:bg-slate-50"
            >
              商品マスタ詳細へ
              <ExternalLink class="h-3.5 w-3.5" />
            </NuxtLink>
          </div>

          <dl class="mt-3 grid grid-cols-2 gap-x-3 gap-y-2 border-t border-slate-100 pt-3 text-xs sm:grid-cols-3">
            <div>
              <dt class="text-slate-400">品番</dt>
              <dd class="font-mono font-semibold text-slate-700">{{ detail.product.productTypeCrd }}</dd>
            </div>
            <div>
              <dt class="text-slate-400">商品記号</dt>
              <dd class="font-mono font-semibold text-slate-700">{{ detail.product.productSign }}</dd>
            </div>
            <div>
              <dt class="text-slate-400">ブランド</dt>
              <dd class="font-semibold text-slate-700">{{ detail.product.brand ?? '—' }}</dd>
            </div>
          </dl>

          <!-- 付属情報（登録がある場合のみ） -->
          <div v-if="detail.product.attachment" class="mt-3 border-t border-slate-100 pt-3">
            <h3 class="text-xs font-semibold text-slate-500">
              付属情報（内容チェックの照合元）
            </h3>
            <dl class="mt-2 grid grid-cols-1 gap-x-3 gap-y-2 text-xs sm:grid-cols-2">
              <div v-for="field in SUBSIDIARY_ATTACHMENT_FIELDS" :key="field.key">
                <dt class="text-slate-400">{{ field.label }}</dt>
                <dd class="whitespace-pre-wrap font-medium text-slate-700">
                  {{ detail.product.attachment[field.key] ?? '—' }}
                </dd>
              </div>
            </dl>
            <p class="mt-2 text-[11px] text-slate-400">
              付属情報の更新日時: {{ formatDateTime(detail.product.attachment.updatedAt) }}
            </p>
          </div>
          <p v-else class="mt-3 border-t border-slate-100 pt-3 text-xs text-slate-400">
            この商品には付属情報が登録されていません（画像と規定のみでチェックしています）。
          </p>
        </div>
        <p v-else class="rounded-xl border border-slate-200 bg-slate-50 p-3 text-xs text-slate-500">
          商品マスタ未選択のチェックです（画像と規定のみでチェックしています）。
        </p>

        <!-- ============ カテゴリ別の指摘（レイアウト / 順番 / 内容） ============ -->
        <div v-if="detail.findings.length > 0" class="space-y-4">
          <section
            v-for="group in findingGroups"
            :key="group.category"
            class="rounded-xl border border-slate-200 bg-white shadow-sm"
            :aria-label="`${group.label}の指摘`"
          >
            <div class="border-b border-slate-100 px-4 py-3">
              <h2 class="text-sm font-bold text-slate-800">{{ group.label }}</h2>
              <p class="mt-0.5 text-xs text-slate-400">{{ group.description }}</p>
            </div>
            <div class="space-y-3 p-4">
              <p v-if="group.findings.length === 0" class="text-xs text-slate-400">
                このカテゴリの確認結果はありません。
              </p>
              <article
                v-for="(finding, index) in group.findings"
                :key="`${group.category}-${index}`"
                class="rounded-lg border border-slate-200 p-3"
              >
                <div class="flex flex-wrap items-center gap-2">
                  <span
                    class="inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-xs font-medium"
                    :class="SUBSIDIARY_FINDING_SEVERITIES[finding.severity].className"
                  >
                    <component
                      :is="SUBSIDIARY_FINDING_SEVERITIES[finding.severity].icon"
                      class="h-3 w-3"
                      aria-hidden="true"
                    />
                    {{ SUBSIDIARY_FINDING_SEVERITIES[finding.severity].label }}
                  </span>
                  <h3 class="min-w-0 flex-1 text-sm font-semibold text-slate-800">
                    {{ finding.title }}
                  </h3>
                </div>
                <p class="mt-2 whitespace-pre-wrap text-sm leading-relaxed text-slate-700">
                  {{ finding.detail }}
                </p>
                <p
                  v-if="finding.suggestion"
                  class="mt-2 whitespace-pre-wrap rounded-lg bg-indigo-50 px-3 py-2 text-xs leading-relaxed text-indigo-700"
                >
                  修正提案: {{ finding.suggestion }}
                </p>
                <p v-if="finding.evidence" class="mt-2 text-xs text-slate-400">
                  根拠: {{ finding.evidence }}
                </p>
              </article>
            </div>
          </section>
        </div>

        <!-- ============ 画像ギャラリー ============ -->
        <section
          v-if="detail.images.length > 0"
          class="rounded-xl border border-slate-200 bg-white shadow-sm"
          aria-label="アップロード画像"
        >
          <div class="border-b border-slate-100 px-4 py-3">
            <h2 class="text-sm font-bold text-slate-800">アップロード画像</h2>
            <p class="mt-0.5 text-xs text-slate-400">
              チェックに使用した画像です（目視確認の補助にお使いください）。
            </p>
          </div>
          <div class="space-y-4 p-4">
            <div
              v-if="imagesLoading"
              class="flex items-center gap-2 text-xs text-slate-500"
            >
              <LoaderCircle class="h-4 w-4 animate-spin text-indigo-500" />
              画像を読み込み中...
            </div>
            <div v-for="group in galleryGroups" :key="group.kind">
              <h3 class="text-xs font-semibold text-slate-500">
                {{ group.label }}画像（{{ formatNumber(group.images.length) }} 枚）
              </h3>
              <ul class="mt-2 grid grid-cols-2 gap-2 sm:grid-cols-3 lg:grid-cols-4">
                <li
                  v-for="image in group.images"
                  :key="image.info.imageId"
                  class="overflow-hidden rounded-lg border border-slate-200 bg-slate-50"
                >
                  <img
                    v-if="image.objectUrl"
                    :src="image.objectUrl"
                    :alt="`${group.label}画像: ${image.info.fileName}`"
                    class="aspect-square w-full bg-white object-contain"
                  >
                  <div
                    v-else
                    class="flex aspect-square w-full flex-col items-center justify-center gap-1 text-slate-400"
                  >
                    <ImageOff class="h-6 w-6" aria-hidden="true" />
                    <span class="text-[10px]">{{ image.failed ? '取得に失敗しました' : '読み込み中…' }}</span>
                  </div>
                  <p
                    class="truncate bg-white px-1.5 py-1 text-[10px] text-slate-500"
                    :title="image.info.fileName"
                  >
                    {{ image.info.fileName }}（{{ formatFileSize(image.info.sizeBytes) }}）
                  </p>
                </li>
              </ul>
            </div>
          </div>
        </section>
      </div>
    </StatusBlock>
  </div>
</template>
