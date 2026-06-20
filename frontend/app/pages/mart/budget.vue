<script setup lang="ts">
/**
 * 予算管理（/mart/budget）。売上予算（両ロール）・仕入予算（バイヤーのみ）を年度×集計軸で登録する。
 *
 * 登録値は他ページで活用する（要件）。当面はバイヤーの全社予算が OTB サマリー（目標売上＝売上予算、
 * 仕入予算＝OTB利用率の分母）へ反映される。永続化はモック段階のため localStorage（useBudget）。
 *
 * 入力は「百万円」単位（億規模の予算でも桁を読みやすくするため）。保存は円換算（×1,000,000）。
 */
import { Trash2, Save, Wallet } from 'lucide-vue-next'
import type { BudgetEntry, BudgetScope } from '~/composables/useBudget'

useHead({ title: '予算管理 | UndeuxSales' })

const { accountType, meta } = useAccountType()
const fields = computed(() => budgetFieldsForRole(accountType.value))

const { load, upsert, remove, entriesForYear, companyBudget } = useBudget()
const { options, optionsError, loadOptions } = useFilters('mart-filter')

const currentYear = new Date().getFullYear()
const years = [currentYear - 2, currentYear - 1, currentYear, currentYear + 1]
const year = ref(currentYear)

const MAN = 1_000_000 // 百万円 → 円

// 入力フォーム（百万円単位）。
const form = reactive<{
  scope: BudgetScope
  code: string | null
  salesMan: number | null
  purchaseMan: number | null
}>({ scope: 'company', code: null, salesMan: null, purchaseMan: null })

const scopeOptions: { value: BudgetScope; label: string }[] = [
  { value: 'company', label: '全社' },
  { value: 'department', label: '部門別' },
  { value: 'businessType', label: '業態別' },
]

/** scope に応じたコード選択肢（部門 / 業態）。'コード: 名称' 形式。 */
const codeOptions = computed<{ value: string; label: string }[]>(() => {
  if (form.scope === 'department') {
    return (options.value?.departments ?? []).map((d) => ({
      value: d.code,
      label: d.name ? `${d.code}: ${d.name}` : d.code,
    }))
  }
  if (form.scope === 'businessType') {
    return (options.value?.businessTypes ?? []).map((b) => ({
      value: b.code,
      label: b.name ? (b.shortName ? `${b.code}: ${b.name} (${b.shortName})` : `${b.code}: ${b.name}`) : b.code,
    }))
  }
  return []
})

/** 集計軸・コード・年度から既存登録を引き当ててフォームに反映する（＝upsert フォーム）。 */
function syncFormFromExisting(): void {
  const existing = entriesForYear(year.value).find(
    (e) => e.scope === form.scope && e.code === (form.scope === 'company' ? null : form.code),
  )
  form.salesMan = existing ? Math.round(existing.salesBudget / MAN) : null
  form.purchaseMan = existing && existing.purchaseBudget !== null ? Math.round(existing.purchaseBudget / MAN) : null
}

watch(() => [year.value, form.scope, form.code], syncFormFromExisting)
// scope を変えたらコード選択をリセット（全社はコード不要）。
watch(() => form.scope, () => { form.code = null })

function labelFor(scope: BudgetScope, code: string | null): string {
  if (scope === 'company') return '全社'
  const opt = codeOptions.value.find((o) => o.value === code)
  return opt?.label ?? code ?? '—'
}

const formError = ref<string | null>(null)

function submit(): void {
  formError.value = null
  if (form.scope !== 'company' && !form.code) {
    formError.value = '対象の部門／業態を選択してください。'
    return
  }
  if (form.salesMan === null || !Number.isFinite(form.salesMan) || form.salesMan < 0) {
    formError.value = '売上予算を正しく入力してください。'
    return
  }
  if (fields.value.purchase && form.purchaseMan !== null && form.purchaseMan < 0) {
    formError.value = '仕入予算は0以上で入力してください。'
    return
  }
  upsert({
    year: year.value,
    scope: form.scope,
    code: form.scope === 'company' ? null : form.code,
    label: labelFor(form.scope, form.scope === 'company' ? null : form.code),
    salesBudget: form.salesMan * MAN,
    purchaseBudget:
      fields.value.purchase && form.purchaseMan !== null ? form.purchaseMan * MAN : null,
  })
}

// 一覧（年度内）。表示直前にラベルを最新の選択肢で解決し直す（取込後に名称が付くケースに追従）。
const rows = computed(() => entriesForYear(year.value))

function resolveLabel(entry: BudgetEntry): string {
  if (entry.scope === 'company') return '全社'
  if (entry.scope === 'department') {
    const d = options.value?.departments.find((x) => x.code === entry.code)
    return d?.name ? `${d.code}: ${d.name}` : entry.label
  }
  const b = options.value?.businessTypes.find((x) => x.code === entry.code)
  return b?.name ? `${b.code}: ${b.name}${b.shortName ? ` (${b.shortName})` : ''}` : entry.label
}

const scopeLabel: Record<BudgetScope, string> = {
  company: '全社',
  department: '部門',
  businessType: '業態',
}

const companyApplied = computed(() => companyBudget(year.value) !== undefined)

onMounted(async () => {
  load()
  await loadOptions()
  syncFormFromExisting()
})
</script>

<template>
  <div class="space-y-4">
    <div>
      <h1 class="text-xl font-bold text-slate-800">予算管理</h1>
      <p class="text-sm text-slate-500">
        {{ meta.label }}として、
        <template v-if="fields.purchase">仕入予算・売上予算</template>
        <template v-else>売上予算</template>
        を年度・集計軸ごとに登録します。登録値は OTB サマリーなど他ページで活用されます。
      </p>
    </div>

    <p v-if="optionsError" class="rounded-lg bg-amber-50 px-3 py-2 text-xs text-amber-700">
      部門・業態の選択肢取得に失敗しました（全社予算は登録できます）: {{ optionsError }}
    </p>

    <!-- 登録フォーム -->
    <div class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
      <div class="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <div>
          <label class="mb-1 block text-xs font-medium text-slate-500">年度</label>
          <select v-model.number="year" class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm">
            <option v-for="y in years" :key="y" :value="y">{{ y }}年</option>
          </select>
        </div>
        <div>
          <label class="mb-1 block text-xs font-medium text-slate-500">集計軸</label>
          <select v-model="form.scope" class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm">
            <option v-for="o in scopeOptions" :key="o.value" :value="o.value">{{ o.label }}</option>
          </select>
        </div>
        <div v-if="form.scope !== 'company'">
          <label class="mb-1 block text-xs font-medium text-slate-500">対象</label>
          <select v-model="form.code" class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm">
            <option :value="null" disabled>選択してください</option>
            <option v-for="o in codeOptions" :key="o.value" :value="o.value">{{ o.label }}</option>
          </select>
        </div>
        <div>
          <label class="mb-1 block text-xs font-medium text-slate-500">売上予算（百万円）</label>
          <input
            v-model.number="form.salesMan"
            type="number"
            min="0"
            inputmode="numeric"
            class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
            placeholder="例: 500"
          >
          <p class="mt-0.5 text-xs text-slate-400">{{ form.salesMan ? formatCurrency(form.salesMan * MAN) : '—' }}</p>
        </div>
        <div v-if="fields.purchase">
          <label class="mb-1 block text-xs font-medium text-slate-500">仕入予算（百万円）</label>
          <input
            v-model.number="form.purchaseMan"
            type="number"
            min="0"
            inputmode="numeric"
            class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
            placeholder="例: 2100"
          >
          <p class="mt-0.5 text-xs text-slate-400">{{ form.purchaseMan ? formatCurrency(form.purchaseMan * MAN) : '—' }}</p>
        </div>
      </div>

      <p v-if="formError" class="mt-2 text-xs text-rose-600">{{ formError }}</p>

      <div class="mt-3 flex items-center gap-2">
        <button
          type="button"
          class="inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700"
          @click="submit"
        >
          <Save class="h-4 w-4" />
          登録 / 更新
        </button>
        <span v-if="!companyApplied" class="text-xs text-amber-600">
          ※ {{ year }}年度の全社予算が未登録です。OTBサマリーは標準値で表示されます。
        </span>
      </div>
    </div>

    <!-- 登録済み一覧 -->
    <div class="space-y-1">
      <h2 class="text-sm font-semibold text-slate-700">{{ year }}年度の登録予算</h2>
      <div v-if="rows.length === 0" class="rounded-xl border border-slate-200 bg-white p-8 text-center text-sm text-slate-400">
        まだ予算が登録されていません。上のフォームから登録してください。
      </div>
      <div v-else class="overflow-hidden rounded-xl border border-slate-200 bg-white">
        <table class="w-full text-sm">
          <thead class="text-slate-500">
            <tr>
              <th class="bg-slate-50 px-4 py-2.5 text-left font-medium">集計軸</th>
              <th class="bg-slate-50 px-4 py-2.5 text-left font-medium">対象</th>
              <th class="bg-slate-50 px-4 py-2.5 text-right font-medium">売上予算</th>
              <th class="bg-slate-50 px-4 py-2.5 text-right font-medium">仕入予算</th>
              <th class="bg-slate-50 px-4 py-2.5 text-right font-medium">操作</th>
            </tr>
          </thead>
          <tbody class="divide-y divide-slate-100">
            <tr v-for="entry in rows" :key="entry.id" class="hover:bg-slate-50">
              <td class="px-4 py-2.5 text-slate-600">
                <span class="inline-flex items-center gap-1.5">
                  <Wallet class="h-3.5 w-3.5 text-slate-400" />
                  {{ scopeLabel[entry.scope] }}
                </span>
              </td>
              <td class="px-4 py-2.5 text-slate-700">{{ resolveLabel(entry) }}</td>
              <td class="px-4 py-2.5 text-right tabular-nums text-slate-700">{{ formatCurrency(entry.salesBudget) }}</td>
              <td class="px-4 py-2.5 text-right tabular-nums text-slate-700">
                {{ entry.purchaseBudget !== null ? formatCurrency(entry.purchaseBudget) : '—' }}
              </td>
              <td class="px-4 py-2.5 text-right">
                <button
                  type="button"
                  class="inline-flex items-center gap-1 rounded-md px-2 py-1 text-xs text-rose-600 hover:bg-rose-50"
                  :aria-label="`${resolveLabel(entry)} の予算を削除`"
                  @click="remove(entry.id)"
                >
                  <Trash2 class="h-3.5 w-3.5" />
                  削除
                </button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>
  </div>
</template>
