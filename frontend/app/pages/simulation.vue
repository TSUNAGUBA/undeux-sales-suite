<script setup lang="ts">
/**
 * 重回帰シミュレーター（/simulation）ページ。
 *
 * 提案1【未来の発注予測】「気象スライダー」: 売上数量 ≈ b0 + b1×気温 + b2×前週売上数量 を
 *   週次系列から重回帰で推定し、来週の予想気温・前週売上をスライダーで動かすと予測がリアルタイムに変わる。
 * 提案2【販促・値引き最適化】「値引き率スライダー」: 気温由来の基準数量に価格弾力性を掛けて
 *   値引き率ごとの売上数量・金額・粗利を予測し、粗利が最大化する値引き率を提示する。
 *
 * 裏側は重回帰＋価格弾力性、表側はスライダーだけの「ゲーム感覚UI」。回帰・予測はフロント射影
 * （バックエンドは週次系列の素材のみ返す）。エリア別配分（提案3）は取引先=店舗軸が本データに
 * 無いため対象外（散布図・本ページともエリア気温は標準気候モデルで近似）。
 */
import { CloudSun, Coins, Package, TrendingUp } from 'lucide-vue-next'
import type { KpiCardItem, TemperatureArea, WeeklySeriesResponse } from '~/types/api'
import type { Observation } from '~/utils/regression'

useHead({ title: '重回帰シミュレーター | UndeuxSales' })

const { toQuery, loadOptions } = useFilters()
const { get } = useApi()

const area = ref<TemperatureArea>('standard')
type TempMeasure = 'avg' | 'max' | 'min'
const tempMeasure = ref<TempMeasure>('max') // 提案1は最高気温を既定にする
const tempMeasureOptions: { value: TempMeasure; label: string }[] = [
  { value: 'avg', label: '週平均気温' },
  { value: 'max', label: '週最高気温' },
  { value: 'min', label: '週最低気温' },
]
const areaOptions = TEMPERATURE_AREAS

const weekly = ref<WeeklySeriesResponse | null>(null)
const loading = ref(true)
const errorMessage = ref<string | null>(null)

// --- スライダー state ---
const inputTemp = ref(22)
const inputPrevQty = ref(0)
const markdownRate = ref(0) // %
const elasticity = ref(1.0) // 値引き1%あたりの数量増加感度（弾力性）

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = null
  try {
    weekly.value = await get<WeeklySeriesResponse>('/api/analysis/weekly-series', {
      ...toQuery(),
      area: area.value,
    })
    syncSliderDefaults()
  } catch (error) {
    errorMessage.value = apiErrorMessage(error)
  } finally {
    loading.value = false
  }
}

function tempOf(p: WeeklySeriesResponse['points'][number]): number {
  return tempMeasure.value === 'max' ? p.tempMax : tempMeasure.value === 'min' ? p.tempMin : p.tempAvg
}

const tempMeasureLabel = computed(
  () => tempMeasureOptions.find((m) => m.value === tempMeasure.value)?.label ?? '週平均気温',
)

// 週次系列から重回帰の観測を作る（i>=1: features=[気温, 前週数量], target=今週数量）。
const observations = computed<Observation[]>(() => {
  const pts = weekly.value?.points ?? []
  const obs: Observation[] = []
  for (let i = 1; i < pts.length; i++) {
    obs.push({ features: [tempOf(pts[i]!), pts[i - 1]!.quantity], target: pts[i]!.quantity })
  }
  return obs
})

const regression = computed(() => multipleLinearRegression(observations.value))

// 単価・原価（期間合計から導出。値引きシミュレーションの金額・粗利換算に使う）。
const unitEconomics = computed(() => {
  const pts = weekly.value?.points ?? []
  let qty = 0
  let amount = 0
  let grossProfit = 0
  for (const p of pts) {
    qty += p.quantity
    amount += p.amount
    grossProfit += p.grossProfit
  }
  if (qty <= 0) return { unitPrice: 0, unitCost: 0 }
  const unitPrice = amount / qty
  const unitCost = (amount - grossProfit) / qty
  return { unitPrice, unitCost }
})

// スライダー初期値・範囲をデータから決める。
function syncSliderDefaults(): void {
  const pts = weekly.value?.points ?? []
  if (pts.length === 0) return
  const last = pts[pts.length - 1]!
  inputTemp.value = Math.round(tempOf(last))
  inputPrevQty.value = last.quantity
}

const tempRange = computed(() => {
  const temps = (weekly.value?.points ?? []).map(tempOf)
  if (temps.length === 0) return { min: 0, max: 40 }
  return { min: Math.floor(Math.min(...temps) - 5), max: Math.ceil(Math.max(...temps) + 5) }
})

const qtyRange = computed(() => {
  const qtys = (weekly.value?.points ?? []).map((p) => p.quantity)
  const max = qtys.length > 0 ? Math.max(...qtys) : 100
  return { min: 0, max: Math.max(10, Math.ceil(max * 1.5)) }
})

// 気温・前週売上から基準数量（値引き 0% 時）を予測する。
function baseQuantity(temp: number, prevQty: number): number {
  const reg = regression.value
  if (!reg) return 0
  return Math.max(0, predict(reg.coefficients, [temp, prevQty]))
}

// 値引き率と弾力性で数量を補正し、金額・粗利を算出する。
function simulate(temp: number, prevQty: number, markdownPct: number): {
  quantity: number
  amount: number
  grossProfit: number
} {
  const base = baseQuantity(temp, prevQty)
  const quantity = Math.max(0, base * (1 + (elasticity.value * markdownPct) / 100))
  const { unitPrice, unitCost } = unitEconomics.value
  const sellingPrice = unitPrice * (1 - markdownPct / 100)
  const amount = quantity * sellingPrice
  const grossProfit = quantity * (sellingPrice - unitCost)
  return { quantity, amount, grossProfit }
}

const prediction = computed(() => simulate(inputTemp.value, inputPrevQty.value, markdownRate.value))
const baseline = computed(() => simulate(inputTemp.value, inputPrevQty.value, 0))

// 粗利が最大化する値引き率（0..70% をスライダー・カーブと同じ 5% 刻みで走査し、
// 提示値が必ず選択可能な点になるようにする）。
const optimalMarkdown = computed(() => {
  let best = 0
  let bestProfit = -Infinity
  for (let m = 0; m <= 70; m += 5) {
    const profit = simulate(inputTemp.value, inputPrevQty.value, m).grossProfit
    if (profit > bestProfit) {
      bestProfit = profit
      best = m
    }
  }
  return { markdown: best, grossProfit: bestProfit }
})

const kpiItems = computed<KpiCardItem[]>(() => {
  if (!regression.value) return []
  const p = prediction.value
  return [
    {
      label: '予測売上数量',
      value: `${formatNumber(p.quantity)} 点`,
      icon: Package,
      accentClass: 'bg-sky-50 text-sky-600',
    },
    {
      label: '予測売上金額',
      value: formatCurrency(p.amount),
      icon: Coins,
      accentClass: 'bg-indigo-50 text-indigo-600',
    },
    {
      label: '予測粗利',
      value: formatCurrency(p.grossProfit),
      icon: TrendingUp,
      accentClass: 'bg-emerald-50 text-emerald-600',
    },
    {
      label: '粗利最大の値引き率',
      value: `${optimalMarkdown.value.markdown}%`,
      icon: CloudSun,
      accentClass: 'bg-amber-50 text-amber-600',
    },
  ]
})

// 値引き率ごとの予測粗利カーブ（0..70%）。粗利最大点を直感的に見せる。
const profitCurveLabels = computed(() => {
  const labels: string[] = []
  for (let m = 0; m <= 70; m += 5) labels.push(`${m}%`)
  return labels
})
const profitCurveSeries = computed(() => {
  const data: number[] = []
  for (let m = 0; m <= 70; m += 5) {
    data.push(Math.round(simulate(inputTemp.value, inputPrevQty.value, m).grossProfit))
  }
  return [{ label: '予測粗利', data, color: '#059669' }]
})

const coefNote = computed(() => {
  const reg = regression.value
  if (!reg) return null
  const [b0, b1, b2] = reg.coefficients
  return `売上数量 ≈ ${formatDecimal(b0 ?? 0, 1)} + ${formatDecimal(b1 ?? 0, 2)}×${tempMeasureLabel.value}`
    + ` + ${formatDecimal(b2 ?? 0, 2)}×前週売上数量　（R²=${formatDecimal(reg.r2, 2)}、${reg.n}週で学習）`
})

const profitDelta = computed(() => prediction.value.grossProfit - baseline.value.grossProfit)

const initialized = ref(false)
// エリア変更はバックエンドの気温が変わるため再取得する。
watch(area, () => {
  if (initialized.value) void load()
})
// 気温種別はフロント射影（同じ週次データから avg/max/min を選ぶだけ）なので再取得しない。
// 回帰・予測は computed が自動再計算する。スライダーの現在値は維持しつつ新しい範囲にクランプする。
watch(tempMeasure, () => {
  const { min, max } = tempRange.value
  inputTemp.value = Math.min(Math.max(inputTemp.value, min), max)
})

onMounted(async () => {
  await loadOptions()
  await load()
  initialized.value = true
})
</script>

<template>
  <div class="space-y-4">
    <div>
      <h1 class="text-xl font-bold text-slate-800">重回帰シミュレーター</h1>
      <p class="text-sm text-slate-500">
        裏側は重回帰＋価格弾力性、表側はスライダー。来週の気温・前週売上・値引き率を動かすと予測が変わります。
      </p>
    </div>

    <div class="flex flex-wrap items-center gap-2">
      <select
        v-model="area"
        class="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-sm"
        aria-label="気温エリア"
      >
        <option v-for="a in areaOptions" :key="a.value" :value="a.value">{{ a.label }}</option>
      </select>
      <select
        v-model="tempMeasure"
        class="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-sm"
        aria-label="気温の種別"
      >
        <option v-for="m in tempMeasureOptions" :key="m.value" :value="m.value">{{ m.label }}</option>
      </select>
    </div>

    <FilterBar @apply="load" />

    <StatusBlock
      :loading="loading"
      :error="errorMessage"
      :empty="(weekly?.points.length ?? 0) === 0"
      empty-message="該当するデータがありません。フィルタや期間を見直してください。"
    >
      <div v-if="!regression" class="rounded-xl border border-amber-200 bg-amber-50 p-4 text-sm text-amber-700">
        重回帰モデルの学習に必要な週数が不足しています（最低4週、かつ説明変数に変化が必要）。期間やフィルタを広げてください。
      </div>
      <div v-else class="space-y-4">
        <p class="rounded-lg bg-slate-50 px-3 py-2 text-xs text-slate-600">{{ coefNote }}</p>

        <!-- スライダー -->
        <div class="grid grid-cols-1 gap-4 rounded-xl border border-slate-200 bg-white p-4 shadow-sm md:grid-cols-2">
          <div>
            <label class="mb-1 flex items-center justify-between text-sm font-medium text-slate-600">
              <span>来週の予想{{ tempMeasureLabel }}</span>
              <span class="tabular-nums text-indigo-600">{{ inputTemp }}℃</span>
            </label>
            <input
              v-model.number="inputTemp"
              type="range"
              :min="tempRange.min"
              :max="tempRange.max"
              step="0.5"
              class="w-full accent-indigo-600"
            >
          </div>
          <div>
            <label class="mb-1 flex items-center justify-between text-sm font-medium text-slate-600">
              <span>前週売上数量</span>
              <span class="tabular-nums text-indigo-600">{{ formatNumber(inputPrevQty) }} 点</span>
            </label>
            <input
              v-model.number="inputPrevQty"
              type="range"
              :min="qtyRange.min"
              :max="qtyRange.max"
              step="1"
              class="w-full accent-indigo-600"
            >
          </div>
          <div>
            <label class="mb-1 flex items-center justify-between text-sm font-medium text-slate-600">
              <span>値引き率</span>
              <span class="tabular-nums text-indigo-600">{{ markdownRate }}%</span>
            </label>
            <input
              v-model.number="markdownRate"
              type="range"
              min="0"
              max="70"
              step="5"
              class="w-full accent-rose-500"
            >
          </div>
          <div>
            <label class="mb-1 flex items-center justify-between text-sm font-medium text-slate-600">
              <span>価格弾力性（値引き1%あたりの数量増）</span>
              <span class="tabular-nums text-indigo-600">{{ formatDecimal(elasticity, 1) }}</span>
            </label>
            <input
              v-model.number="elasticity"
              type="range"
              min="0"
              max="3"
              step="0.1"
              class="w-full accent-rose-500"
            >
          </div>
        </div>

        <!-- 予測 KPI -->
        <div class="grid grid-cols-2 gap-3 lg:grid-cols-4">
          <KpiCard
            v-for="item in kpiItems"
            :key="item.label"
            :label="item.label"
            :value="item.value"
            :icon="item.icon"
            :accent-class="item.accentClass"
          />
        </div>

        <p class="rounded-lg bg-emerald-50 px-3 py-2 text-sm text-emerald-800">
          値引き 0% と比べた予測粗利の差: <strong>{{ profitDelta >= 0 ? '+' : '' }}{{ formatCurrency(profitDelta) }}</strong>
          ／ 粗利が最大になるのは <strong>{{ optimalMarkdown.markdown }}% 引き</strong>（{{ formatCurrency(optimalMarkdown.grossProfit) }}）です。
        </p>

        <LineChartCard
          title="値引き率ごとの予測粗利"
          :labels="profitCurveLabels"
          :series="profitCurveSeries"
        />
      </div>
    </StatusBlock>
  </div>
</template>
