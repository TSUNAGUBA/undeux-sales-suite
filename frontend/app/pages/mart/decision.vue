<script setup lang="ts">
/**
 * 意思決定オントロジー・ビュー（/mart/decision）ページ。
 *
 * オントロジー3次元（①意味／②関連／③アクション＆制約）を滞留SKU「HE8800」で見せ、
 * ③制約の「✗」で打ち手が潰れ（グレー＋取消線）、○/△ の打ち手だけが下の「選択肢カード」へ
 * 昇格する流れを体感させるビュー。参照実装は
 * `refference/意思決定オントロジー・ビュー（概念モック）/index.html`（動く概念モック）。
 *
 * データ源は本フェーズではダミー固定（utils/decisionOntologyMock）。実データ接続・実行処理・
 * 制約ルールの実体化は次フェーズ対象外。目的関数・打ち手・選択肢は、画面右上のアプリ共通トグル
 * （メーカー／小売＝useAccountType）の状態に連動して丸ごと切り替わる（本ビュー独自のトグルは持たない）。
 */
import { ChevronDown, ChevronRight, Info, ListChecks } from 'lucide-vue-next'
import type { ActionStatus, SemanticAttr } from '~/utils/decisionOntologyMock'

useHead({ title: '意思決定オントロジー・ビュー | UndeuxSales' })

const { accountType, meta } = useAccountType()

// アプリ共通トグル（メーカー／小売）の状態に連動してペルソナ別の意思決定セットを切り替える。
const persona = computed(() => personaForAccountType(accountType.value))
const decision = computed(() => DECISION_DATA[persona.value])

// ① 意味（セマンティク）: 主役オブジェクトの単一定義から表示行を導出する（書式は format ユーティリティで統一）。
// 主役オブジェクトは固定ダミーのためリアクティブ依存を持たない。定数として一度だけ組み立てる。
const o = DECISION_OBJECT
const semanticRows: SemanticAttr[] = [
  { key: '品番3桁', value: o.hinban3 },
  { key: '季節区分', value: o.season },
  { key: 'カテゴリ', value: o.category },
  { key: '原価', value: formatCurrency(o.cost) },
  { key: '売価', value: formatCurrency(o.price) },
  { key: '粗利率', value: formatRatioAsPercent(o.grossMarginRate) },
  { key: '消化率', value: formatRatioAsPercent(o.sellThroughRate) },
  { key: '在庫日数', value: `${o.stockDays}日` },
  { key: '状態', value: o.status },
]

// サマリー帯の KV（状態は別枠でドット付き表示）。
const summaryKvs: { label: string; value: string }[] = [
  { label: '消化率', value: formatRatioAsPercent(o.sellThroughRate) },
  { label: '在庫日数', value: `${o.stockDays}日` },
  { label: '在庫', value: `${formatNumber(o.stockQty)}点` },
  { label: '在庫金額', value: formatCurrency(o.stockValue) },
  { label: '粗利率', value: formatRatioAsPercent(o.grossMarginRate) },
]

// ② 関連: クリックでミニ情報（③制約の出所など）を開閉するインデックス集合。
const openLinks = ref<Set<number>>(new Set())
function toggleLink(index: number): void {
  const next = new Set(openLinks.value)
  if (next.has(index)) next.delete(index)
  else next.add(index)
  openLinks.value = next
}

/** ③ 昇格タグ（→ 選択肢 X）の色。○=緑／△=橙。✗ は昇格しないため使わない。 */
function promoteClass(status: ActionStatus): string {
  return status === 'warn' ? 'bg-amber-500' : 'bg-emerald-600'
}

// [判断＆実行] は本モックでは飾り。押下時にトーストで次フェーズである旨のみ通知する。
const toastMessage = ref<string | null>(null)
let toastTimer: ReturnType<typeof setTimeout> | null = null
function showExecToast(): void {
  toastMessage.value = '実行は次フェーズ（本モックの対象外）'
  if (toastTimer) clearTimeout(toastTimer)
  toastTimer = setTimeout(() => {
    toastMessage.value = null
  }, 2200)
}
onBeforeUnmount(() => {
  if (toastTimer) clearTimeout(toastTimer)
})
</script>

<template>
  <div class="space-y-4">
    <!-- ヘッダー: タイトル・対象オブジェクト・現在ペルソナ（共通トグル連動） -->
    <div>
      <div class="flex flex-wrap items-center gap-x-3 gap-y-1">
        <h1 class="flex items-center gap-2 text-xl font-bold text-slate-800">
          <ListChecks class="h-5 w-5 text-indigo-500" />意思決定オントロジー・ビュー
        </h1>
        <span class="font-mono text-sm text-slate-400">対象オブジェクト: SKU「{{ DECISION_OBJECT.sku }}」</span>
      </div>
      <p class="mt-1 text-sm text-slate-500">
        目的関数: <strong class="text-slate-700">{{ decision.objective }}</strong> —
        <strong class="text-slate-700">アクション＆制約</strong> の「✗」で打ち手が潰れ、<strong class="text-slate-700">○/△だけが選択肢カードへ昇格</strong>する流れを見るビュー。
      </p>
      <p class="mt-1 inline-flex items-center gap-1.5 rounded-lg bg-amber-50 px-2.5 py-1 text-xs text-amber-700">
        <Info class="h-3.5 w-3.5 shrink-0" />
        概念モック・ダミーデータ（実データ接続／実行処理は対象外）。表示中は<strong class="mx-0.5">{{ meta.short }}</strong>視点 — 右上の【メーカー／小売】切替で目的関数・打ち手・選択肢が丸ごと変わります。
      </p>
    </div>

    <!-- サマリー帯 -->
    <div class="flex flex-wrap items-center gap-x-6 gap-y-2 rounded-xl border border-slate-200 bg-white p-3 px-4 shadow-sm">
      <span class="inline-flex items-center gap-2 font-bold text-slate-800">
        <span class="h-2.5 w-2.5 rounded-full bg-amber-400 shadow-[0_0_7px_rgba(245,158,11,.6)]" />
        状態: {{ DECISION_OBJECT.status }}
      </span>
      <span v-for="kv in summaryKvs" :key="kv.label" class="text-xs text-slate-500">
        {{ kv.label }} <strong class="font-mono text-sm text-slate-800">{{ kv.value }}</strong>
      </span>
    </div>

    <!-- オントロジー3次元グリッド -->
    <div class="grid grid-cols-1 gap-3.5 lg:grid-cols-[1fr_1fr_1.25fr]">
      <!-- ① 意味 -->
      <section class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
        <h2 class="mb-3 flex flex-wrap items-center gap-x-2 text-sm font-bold text-slate-800">
          ① 意味
          <span class="text-[10.5px] font-medium text-slate-400">セマンティク／このオブジェクトが何か</span>
        </h2>
        <div>
          <div
            v-for="row in semanticRows"
            :key="row.key"
            class="flex items-center justify-between gap-2.5 border-b border-dashed border-slate-100 py-1.5 last:border-b-0"
          >
            <span class="text-xs text-slate-500">{{ row.key }}</span>
            <span class="font-mono text-xs text-slate-700">{{ row.value }}</span>
          </div>
        </div>
      </section>

      <!-- ② 関連 -->
      <section class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
        <h2 class="mb-3 flex flex-wrap items-center gap-x-2 text-sm font-bold text-slate-800">
          ② 関連
          <span class="text-[10.5px] font-medium text-slate-400">オントロジー・リンク／何と繋がるか</span>
        </h2>
        <div class="space-y-1.5">
          <template v-for="(link, i) in ONTOLOGY_LINKS" :key="link.label">
            <button
              type="button"
              class="flex w-full items-center justify-between gap-2 rounded-lg border border-slate-200 bg-slate-50/60 px-2.5 py-2 text-left text-xs transition-colors hover:border-indigo-400 hover:bg-indigo-50/60"
              :aria-expanded="openLinks.has(i)"
              :aria-controls="`decision-link-info-${i}`"
              @click="toggleLink(i)"
            >
              <span class="min-w-0 truncate text-slate-700">
                {{ link.label }} <span class="text-slate-400">{{ link.to }}</span>
              </span>
              <ChevronRight
                class="h-3.5 w-3.5 shrink-0 text-indigo-500 transition-transform"
                :class="openLinks.has(i) ? 'rotate-90' : ''"
              />
            </button>
            <p
              v-if="openLinks.has(i)"
              :id="`decision-link-info-${i}`"
              class="rounded-lg border border-dashed border-indigo-200 bg-indigo-50/60 px-2.5 py-1.5 text-xs text-slate-500"
            >
              {{ link.info }}
            </p>
          </template>
        </div>
      </section>

      <!-- ③ アクション＆制約 -->
      <section class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
        <h2 class="mb-3 flex flex-wrap items-center gap-x-2 text-sm font-bold text-slate-800">
          ③ アクション＆制約
          <span class="text-[10.5px] font-medium text-slate-400">取りうる打ち手 ＋ 制約</span>
        </h2>
        <div class="space-y-1.5">
          <div
            v-for="action in decision.actions"
            :key="action.name"
            class="flex items-center gap-2.5 rounded-lg border border-slate-200 px-2.5 py-2"
            :class="action.status === 'ng' ? 'bg-slate-50 opacity-50' : ''"
          >
            <span
              class="inline-flex h-6 min-w-6 shrink-0 items-center justify-center rounded-full px-1.5 text-xs font-extrabold"
              :class="ACTION_STATUS_META[action.status].pillClass"
            >{{ ACTION_STATUS_META[action.status].symbol }}</span>
            <span
              class="shrink-0 text-sm font-semibold text-slate-700"
              :class="action.status === 'ng' ? 'line-through' : ''"
            >{{ action.name }}</span>
            <span class="min-w-0 flex-1 text-xs text-slate-500">{{ action.why }}</span>
            <span
              v-if="action.slot"
              class="shrink-0 rounded px-1.5 py-0.5 text-[10px] font-bold text-white"
              :class="promoteClass(action.status)"
            >→ 選択肢 {{ action.slot }}</span>
          </div>
        </div>
      </section>
    </div>

    <!-- 収束バンド -->
    <div class="flex items-center justify-center gap-1.5 text-xs font-bold text-slate-500">
      制約を通った打ち手だけが昇格
      <ChevronDown class="h-4 w-4 text-indigo-500" />
    </div>

    <!-- 選択肢カード -->
    <section class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
      <p class="mb-3 text-sm font-bold text-slate-800">{{ decision.issue }}</p>
      <div class="grid grid-cols-1 gap-3 md:grid-cols-3">
        <div
          v-for="option in decision.options"
          :key="option.slot"
          class="relative rounded-xl border bg-slate-50/40 p-3.5"
          :class="option.recommended ? 'border-indigo-500 ring-2 ring-indigo-500/15' : 'border-slate-200'"
        >
          <span
            v-if="option.recommended"
            class="absolute right-3 top-3 text-[11px] font-bold text-indigo-600"
          >★ AI推奨</span>
          <span
            class="mb-2 inline-block rounded px-2 py-0.5 text-[10.5px] font-bold text-white"
            :class="option.recommended ? 'bg-indigo-600' : 'bg-slate-400'"
          >選択肢 {{ option.slot }}</span>
          <h3 class="mb-2 text-sm font-bold text-slate-800">{{ option.title }}</h3>
          <div class="mb-2 rounded-lg bg-slate-100/80 px-2.5 py-2">
            <p class="mb-0.5 text-[10px] font-semibold text-slate-400">予測影響</p>
            <p
              v-for="(line, i) in option.prediction"
              :key="i"
              class="font-mono text-xs text-slate-700"
            >{{ line }}</p>
          </div>
          <p class="mb-3 text-xs text-slate-500">
            <template v-for="(seg, i) in option.basis" :key="i">
              <strong v-if="seg.strong" class="font-semibold text-slate-700">{{ seg.text }}</strong>
              <template v-else>{{ seg.text }}</template>
            </template>
          </p>
          <button
            type="button"
            class="flex w-full items-center justify-center gap-1 rounded-lg border border-indigo-500 px-3 py-2 text-xs font-bold transition-colors"
            :class="option.recommended ? 'bg-indigo-600 text-white hover:bg-indigo-700' : 'bg-white text-indigo-600 hover:bg-indigo-50'"
            @click="showExecToast"
          >
            判断＆実行 <ChevronRight class="h-3.5 w-3.5" />
          </button>
        </div>
      </div>

      <p class="mt-3 rounded-lg bg-slate-50 px-3 py-2.5 text-xs text-slate-500">
        <template v-for="(seg, i) in decision.whyRecommend" :key="i">
          <strong v-if="seg.strong" class="font-semibold text-slate-700">{{ seg.text }}</strong>
          <template v-else>{{ seg.text }}</template>
        </template>
      </p>
    </section>

    <p class="text-center text-[11px] text-slate-400">
      ※ 概念モック・ダミーデータ。実データ接続／実行処理／制約ルールの実体化は次フェーズで定義します。
    </p>

    <!-- トースト（[判断＆実行] は飾り）。
         ライブリージョン（role="status"）は常設し、中身だけを出し入れする。
         リージョンの挿入と同時にテキストが現れると読み上げないスクリーンリーダーがあるため。 -->
    <div
      class="pointer-events-none fixed bottom-6 left-1/2 z-50 -translate-x-1/2"
      role="status"
      aria-live="polite"
    >
      <Transition
        enter-active-class="transition duration-200 ease-out"
        enter-from-class="translate-y-3 opacity-0"
        leave-active-class="transition duration-200 ease-in"
        leave-to-class="translate-y-3 opacity-0"
      >
        <div
          v-if="toastMessage"
          class="rounded-xl bg-slate-800 px-4 py-2.5 text-sm font-bold text-white shadow-lg"
        >
          {{ toastMessage }}
        </div>
      </Transition>
    </div>
  </div>
</template>
