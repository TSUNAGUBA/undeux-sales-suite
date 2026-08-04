<script setup lang="ts">
import {
  CircleX,
  LoaderCircle,
  Plus,
  Settings2,
  Sparkles,
  Trash2,
} from 'lucide-vue-next'
import type {
  ApiError,
  CareIcon,
  CareIconCatalogResponse,
  ManualCheckDetail,
  ManualCheckInput,
  ManualInputField,
  TagPattern,
} from '~/types/api'

/**
 * 手入力チェックの新規実行フォーム。
 * タグパターンを呼び出してフォームを動的構成し、手入力＋タグ画像を multipart で送信する。
 * 送信後は結果詳細（/subsidiary-check/manual/{checkId}）へ遷移し、そちらの自動ポーリングで
 * 突合結果（completed / failed）を表示する（既存の画像チェックと同じ非同期方式）。
 */
const emit = defineEmits<{ (e: 'go-patterns'): void }>()

const { get, post } = useApi()

// ---- パターン・アイコンマスタの取得 ----

const patterns = ref<TagPattern[]>([])
const careIcons = ref<CareIcon[]>([])
const loading = ref(true)
const loadError = ref<string | null>(null)

const selectedPatternId = ref<string | null>(null)

async function loadCatalogs(): Promise<void> {
  loading.value = true
  loadError.value = null
  try {
    const [patternList, iconCatalog] = await Promise.all([
      get<TagPattern[]>('/api/subsidiary-check/tag-patterns'),
      get<CareIconCatalogResponse>('/api/subsidiary-check/manual/care-icons'),
    ])
    patterns.value = patternList
    careIcons.value = iconCatalog.icons
    // 既定は先頭パターン（seed の標準タグ）。パターンが変わったらフォームを組み直す。
    if (patternList.length > 0 && selectedPatternId.value === null) {
      applyPattern(patternList[0]!)
    }
  } catch (error) {
    console.error('[manual-check] パターン/アイコンの取得に失敗しました:', error)
    loadError.value = apiErrorMessage(error)
  } finally {
    loading.value = false
  }
}

const selectedPattern = computed(
  () => patterns.value.find((p) => p.patternId === selectedPatternId.value) ?? null,
)

// ---- 動的フォームの状態 ----

interface RatioRow {
  material: string
  percent: string
}

interface FieldState {
  def: TagPattern['fields'][number]
  /** text/number/careIcon の値（careIcon はアイコンコード列）。 */
  values: string[]
  /** ratio（組成）の成分。UI では percent は文字列で持ち、送信時に数値化する。 */
  ratios: RatioRow[]
}

const fieldStates = ref<FieldState[]>([])

function buildFieldState(def: TagPattern['fields'][number]): FieldState {
  if (def.compare === 'ratio') {
    return { def, values: [], ratios: [{ material: '', percent: '' }] }
  }
  if (def.compare === 'careIcon') {
    return { def, values: [], ratios: [] }
  }
  // text / number（単数・複数とも1行から開始）
  return { def, values: [''], ratios: [] }
}

function applyPattern(pattern: TagPattern): void {
  selectedPatternId.value = pattern.patternId
  fieldStates.value = pattern.fields.map(buildFieldState)
}

function onPatternChange(event: Event): void {
  const id = (event.target as HTMLSelectElement).value
  const pattern = patterns.value.find((p) => p.patternId === id)
  if (pattern) applyPattern(pattern)
}

// ---- 複数値・組成行の増減 ----

function addValue(state: FieldState): void {
  state.values.push('')
}

function removeValue(state: FieldState, index: number): void {
  state.values.splice(index, 1)
  if (state.values.length === 0) state.values.push('')
}

function addRatio(state: FieldState): void {
  state.ratios.push({ material: '', percent: '' })
}

function removeRatio(state: FieldState, index: number): void {
  state.ratios.splice(index, 1)
  if (state.ratios.length === 0) state.ratios.push({ material: '', percent: '' })
}

// ---- 対象商品ラベル（任意・自由記述） ----

const productLabel = ref('')

// ---- タグ画像アップロード（副資材チェックと同じ検証・プレビュー） ----

interface UploadImage {
  file: File
  previewUrl: string
}

const tagImages = ref<UploadImage[]>([])
const fileErrors = ref<string[]>([])

function totalSelectedBytes(): number {
  return tagImages.value.reduce((sum, image) => sum + image.file.size, 0)
}

function addFiles(fileList: FileList | null): void {
  const errors: string[] = []
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
        `${file.name}: 画像の合計サイズを ${formatFileSize(SUBSIDIARY_TOTAL_IMAGE_MAX_BYTES)} 以内にしてください。`,
      )
      continue
    }
    if (tagImages.value.length >= MANUAL_TAG_MAX_COUNT) {
      errors.push(`タグ画像は最大 ${MANUAL_TAG_MAX_COUNT} 枚までです（超過分は追加されません）。`)
      break
    }
    tagImages.value = [...tagImages.value, { file, previewUrl: URL.createObjectURL(file) }]
  }
  fileErrors.value = errors
}

function onFileChange(event: Event): void {
  const input = event.target as HTMLInputElement
  addFiles(input.files)
  input.value = ''
}

function removeImage(index: number): void {
  const removed = tagImages.value[index]
  if (!removed) return
  URL.revokeObjectURL(removed.previewUrl)
  tagImages.value = tagImages.value.filter((_, i) => i !== index)
}

onBeforeUnmount(() => {
  for (const image of tagImages.value) URL.revokeObjectURL(image.previewUrl)
})

// ---- 送信 ----

const submitting = ref(false)
const formError = ref<string | null>(null)
const submitError = ref<ApiError | null>(null)

function parsePercent(value: string): number | null {
  const trimmed = value.trim()
  if (trimmed === '') return null
  const n = Number(trimmed)
  return Number.isFinite(n) ? n : null
}

/** フォーム状態から送信用の ManualCheckInput を組み立てる。 */
function buildInput(): ManualCheckInput {
  const fields: ManualInputField[] = fieldStates.value.map((state) => {
    const def = state.def
    if (def.compare === 'ratio') {
      const ratios = state.ratios
        .map((r) => ({ material: r.material.trim(), percent: parsePercent(r.percent) }))
        .filter((r) => r.material !== '' || r.percent !== null)
      return { key: def.key, label: def.label, valueType: def.valueType, multiple: def.multiple, compare: def.compare, values: [], ratios }
    }
    // text / number / careIcon
    const values = state.values.map((v) => v.trim()).filter((v) => v !== '')
    return { key: def.key, label: def.label, valueType: def.valueType, multiple: def.multiple, compare: def.compare, values, ratios: [] }
  })
  return {
    patternId: selectedPattern.value?.patternId ?? null,
    patternName: selectedPattern.value?.name ?? '',
    fields,
  }
}

function fieldHasValue(state: FieldState): boolean {
  return state.def.compare === 'ratio'
    ? state.ratios.some((r) => r.material.trim() !== '' || r.percent.trim() !== '')
    : state.values.some((v) => v.trim() !== '')
}

const canSubmit = computed(
  () => !submitting.value && fieldStates.value.length > 0 && tagImages.value.length > 0,
)

async function runCheck(): Promise<void> {
  if (submitting.value) return
  formError.value = null
  submitError.value = null

  if (tagImages.value.length < 1) {
    formError.value = 'タグ画像を1枚以上アップロードしてください。'
    return
  }
  // 必須項目・突合対象の有無をクライアント側でも確認する（最終判定はバックエンド）。
  const missingRequired = fieldStates.value.find((s) => s.def.required && !fieldHasValue(s))
  if (missingRequired) {
    formError.value = `必須項目「${missingRequired.def.label || missingRequired.def.key}」を入力してください。`
    return
  }
  if (!fieldStates.value.some(fieldHasValue)) {
    formError.value = '少なくとも1つの項目に値を入力してください（突合対象がありません）。'
    return
  }

  submitting.value = true
  let createdCheckId: string | null = null
  try {
    const formData = new FormData()
    formData.append('input', JSON.stringify(buildInput()))
    if (productLabel.value.trim()) formData.append('productLabel', productLabel.value.trim())
    for (const image of tagImages.value) {
      formData.append('tagImages', image.file, image.file.name)
    }
    const detail = await post<ManualCheckDetail>('/api/subsidiary-check/manual', formData)
    createdCheckId = detail.summary.checkId
  } catch (error) {
    console.error('[manual-check] 手入力チェックの実行に失敗しました:', error)
    submitError.value =
      extractApiError(error) ?? { errorCode: 'UNKNOWN', summary: apiErrorMessage(error), remedy: '' }
    submitting.value = false
  }

  // 遷移は try の外（ナビゲーション中断を「実行失敗」と誤認させないため。既存 index.vue と同じ判断）。
  if (createdCheckId !== null) {
    await navigateTo(`/subsidiary-check/manual/${createdCheckId}`)
  }
}

onMounted(() => {
  void loadCatalogs()
})
</script>

<template>
  <section aria-label="手入力チェック" class="space-y-4">
    <StatusBlock
      :loading="loading"
      :error="loadError"
      :empty="!loading && !loadError && patterns.length === 0"
      empty-message="タグパターンがありません。「タグパターン設定」から作成してください。"
    >
      <div class="space-y-4">
        <!-- パターン選択 -->
        <div class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
          <div class="flex flex-wrap items-end justify-between gap-3">
            <div class="min-w-0 flex-1">
              <label for="manual-pattern" class="text-sm font-semibold text-slate-700">
                タグパターン
              </label>
              <p class="mt-0.5 text-xs text-slate-500">
                入力フォームの項目構成を決めるパターンです。項目の追加・型・突合方式は「タグパターン設定」で編集できます。
              </p>
              <select
                id="manual-pattern"
                class="mt-2 w-full max-w-md rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-700 focus:border-indigo-400 focus:outline-none"
                :value="selectedPatternId ?? ''"
                @change="onPatternChange"
              >
                <option v-for="pattern in patterns" :key="pattern.patternId" :value="pattern.patternId">
                  {{ pattern.name }}
                </option>
              </select>
              <p v-if="selectedPattern?.description" class="mt-1 text-xs text-slate-400">
                {{ selectedPattern.description }}
              </p>
            </div>
            <button
              type="button"
              class="inline-flex shrink-0 items-center gap-1.5 rounded-lg border border-slate-300 px-3 py-2 text-xs font-medium text-slate-600 hover:bg-slate-50"
              @click="emit('go-patterns')"
            >
              <Settings2 class="h-4 w-4" />
              パターンを編集
            </button>
          </div>
        </div>

        <!-- 対象商品ラベル（任意） -->
        <div class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
          <label for="manual-product-label" class="text-sm font-semibold text-slate-700">
            対象商品ラベル（任意）
          </label>
          <p class="mt-0.5 text-xs text-slate-500">品番・商品名など、履歴で識別しやすい表示名を任意で入力できます。</p>
          <input
            id="manual-product-label"
            v-model="productLabel"
            type="text"
            placeholder="例: WC8945CB スリッパ"
            class="mt-2 w-full max-w-md rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-700 placeholder:text-slate-400 focus:border-indigo-400 focus:outline-none"
          >
        </div>

        <!-- 動的入力フォーム -->
        <div class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
          <h3 class="text-sm font-semibold text-slate-700">手入力項目</h3>
          <p class="mt-0.5 text-xs text-slate-500">
            タグに表示されている内容を入力してください。入力値とアップロード画像の読取値を項目ごとに突き合わせます。
          </p>

          <div class="mt-3 space-y-4">
            <div
              v-for="state in fieldStates"
              :key="state.def.key"
              class="border-t border-slate-100 pt-3 first:border-t-0 first:pt-0"
            >
              <div class="flex flex-wrap items-center gap-2">
                <label class="text-sm font-medium text-slate-700">{{ state.def.label || state.def.key }}</label>
                <span v-if="state.def.required" class="rounded bg-rose-50 px-1.5 py-0.5 text-[10px] text-rose-600">必須</span>
                <span class="rounded bg-slate-100 px-1.5 py-0.5 text-[10px] text-slate-500">
                  {{ manualCompareModeLabel(state.def.compare) }}
                </span>
              </div>

              <!-- 洗濯表示（アイコン選択） -->
              <div v-if="state.def.compare === 'careIcon'" class="mt-2">
                <CareIconPicker v-model="state.values" :icons="careIcons" :disabled="submitting" />
              </div>

              <!-- 組成（材質＋割合） -->
              <div v-else-if="state.def.compare === 'ratio'" class="mt-2 space-y-2">
                <div
                  v-for="(row, index) in state.ratios"
                  :key="index"
                  class="flex flex-wrap items-center gap-2"
                >
                  <span class="text-xs text-slate-400">{{ index + 1 }}.</span>
                  <input
                    v-model="row.material"
                    type="text"
                    placeholder="材質（例: 綿）"
                    class="min-w-0 flex-1 rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-400 focus:outline-none"
                    :disabled="submitting"
                  >
                  <div class="flex items-center gap-1">
                    <input
                      v-model="row.percent"
                      type="text"
                      inputmode="decimal"
                      placeholder="割合"
                      class="w-20 rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-400 focus:outline-none"
                      :disabled="submitting"
                    >
                    <span class="text-sm text-slate-400">%</span>
                  </div>
                  <button
                    type="button"
                    class="rounded p-1.5 text-slate-400 hover:text-rose-600 disabled:opacity-40"
                    :disabled="submitting"
                    aria-label="この成分を削除"
                    @click="removeRatio(state, index)"
                  >
                    <Trash2 class="h-3.5 w-3.5" />
                  </button>
                </div>
                <button
                  type="button"
                  class="inline-flex items-center gap-1 text-xs font-medium text-indigo-600 hover:text-indigo-800 disabled:opacity-40"
                  :disabled="submitting || state.ratios.length >= MANUAL_MAX_VALUES_PER_FIELD"
                  @click="addRatio(state)"
                >
                  <Plus class="h-3.5 w-3.5" /> 成分を追加
                </button>
              </div>

              <!-- 複数入力（文字列） -->
              <div v-else-if="state.def.multiple" class="mt-2 space-y-2">
                <div v-for="(_, index) in state.values" :key="index" class="flex items-center gap-2">
                  <span class="text-xs text-slate-400">{{ index + 1 }}.</span>
                  <input
                    v-model="state.values[index]"
                    type="text"
                    class="min-w-0 flex-1 rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-400 focus:outline-none"
                    :disabled="submitting"
                  >
                  <button
                    type="button"
                    class="rounded p-1.5 text-slate-400 hover:text-rose-600 disabled:opacity-40"
                    :disabled="submitting"
                    aria-label="この値を削除"
                    @click="removeValue(state, index)"
                  >
                    <Trash2 class="h-3.5 w-3.5" />
                  </button>
                </div>
                <button
                  type="button"
                  class="inline-flex items-center gap-1 text-xs font-medium text-indigo-600 hover:text-indigo-800 disabled:opacity-40"
                  :disabled="submitting || state.values.length >= MANUAL_MAX_VALUES_PER_FIELD"
                  @click="addValue(state)"
                >
                  <Plus class="h-3.5 w-3.5" /> 値を追加
                </button>
              </div>

              <!-- 単一入力（文字列 / 数値） -->
              <div v-else class="mt-2">
                <input
                  v-model="state.values[0]"
                  :type="state.def.valueType === 'number' ? 'text' : 'text'"
                  :inputmode="state.def.valueType === 'number' ? 'decimal' : undefined"
                  class="w-full max-w-md rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-400 focus:outline-none"
                  :disabled="submitting"
                >
              </div>
            </div>
          </div>
        </div>

        <!-- タグ画像アップロード -->
        <div class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
          <div class="flex flex-wrap items-baseline justify-between gap-2">
            <h3 class="text-sm font-semibold text-slate-700">タグ画像</h3>
            <span class="text-xs text-slate-400">{{ tagImages.length }} / {{ MANUAL_TAG_MAX_COUNT }} 枚</span>
          </div>
          <p class="mt-0.5 text-xs text-slate-500">
            検査対象のタグ画像（1〜{{ MANUAL_TAG_MAX_COUNT }}枚・必須）。JPEG / PNG・各 {{ formatFileSize(SUBSIDIARY_IMAGE_MAX_BYTES) }} 以下・合計 {{ formatFileSize(SUBSIDIARY_TOTAL_IMAGE_MAX_BYTES) }} 以内。
          </p>
          <input
            type="file"
            :accept="SUBSIDIARY_IMAGE_ACCEPT"
            multiple
            class="mt-3 w-full text-sm text-slate-600 file:mr-3 file:rounded-lg file:border-0 file:bg-slate-100 file:px-3 file:py-2 file:text-sm file:font-medium"
            :disabled="submitting"
            aria-label="タグ画像を選択"
            @change="onFileChange"
          >
          <ul v-if="tagImages.length > 0" class="mt-3 grid grid-cols-3 gap-2 sm:grid-cols-4">
            <li
              v-for="(image, index) in tagImages"
              :key="image.previewUrl"
              class="relative overflow-hidden rounded-lg border border-slate-200 bg-slate-50"
            >
              <img :src="image.previewUrl" :alt="`タグ画像 ${index + 1}`" class="aspect-square w-full object-cover">
              <button
                type="button"
                class="absolute right-1 top-1 rounded-md bg-white/90 p-1.5 text-slate-500 shadow-sm hover:bg-rose-50 hover:text-rose-600"
                :disabled="submitting"
                :aria-label="`${image.file.name} を削除`"
                @click="removeImage(index)"
              >
                <Trash2 class="h-3.5 w-3.5" />
              </button>
            </li>
          </ul>
        </div>

        <ul
          v-if="fileErrors.length > 0"
          class="list-disc rounded-lg border border-amber-200 bg-amber-50 py-2 pl-8 pr-3 text-xs text-amber-700"
          role="alert"
        >
          <li v-for="(message, index) in fileErrors" :key="index">{{ message }}</li>
        </ul>

        <p
          v-if="formError"
          class="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700"
          role="alert"
        >
          {{ formError }}
        </p>

        <div v-if="submitError" class="rounded-lg bg-red-50 p-3 text-sm text-red-700">
          <div class="flex items-start gap-2">
            <CircleX class="h-5 w-5 shrink-0" />
            <div>
              <p class="font-medium">[{{ submitError.errorCode }}] {{ submitError.summary }}</p>
              <p v-if="submitError.detail" class="text-xs text-red-600">{{ submitError.detail }}</p>
              <p v-if="submitError.remedy" class="text-xs text-red-500">{{ submitError.remedy }}</p>
            </div>
          </div>
        </div>

        <div class="flex flex-wrap items-center gap-3">
          <button
            type="button"
            class="inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700 disabled:opacity-50"
            :disabled="!canSubmit"
            @click="runCheck"
          >
            <LoaderCircle v-if="submitting" class="h-4 w-4 animate-spin" />
            <Sparkles v-else class="h-4 w-4" />
            {{ submitting ? '登録中…' : '突合チェックを実行' }}
          </button>
          <p v-if="!submitting" class="text-xs text-slate-400">
            実行すると画像が保存され、すぐに結果へ移動します（AI によるタグ読取と突合はバックグラウンドで実行されます）。
          </p>
        </div>
      </div>
    </StatusBlock>
  </section>
</template>
