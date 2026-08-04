<script setup lang="ts">
import {
  CircleX,
  LoaderCircle,
  Pencil,
  Plus,
  RefreshCw,
  Save,
  Trash2,
  X,
} from 'lucide-vue-next'
import type {
  ApiError,
  ManualFieldCompareMode,
  ManualFieldValueType,
  TagPattern,
  TagPatternField,
  TagPatternUpsert,
} from '~/types/api'

/**
 * タグパターン設定（手入力フォームの構成テンプレートの CRUD）。
 * 項目の組合せ・型（文字列/数値）・単複・突合方式（文字列/組成/洗濯アイコン）を編集する。
 * SoT はバックエンド（subsidiary_tag_pattern）。
 */
const { get, post, put, del } = useApi()

const patterns = ref<TagPattern[]>([])
const loading = ref(true)
const loadError = ref<string | null>(null)

async function loadPatterns(): Promise<void> {
  loading.value = true
  loadError.value = null
  try {
    patterns.value = await get<TagPattern[]>('/api/subsidiary-check/tag-patterns', { includeInactive: true })
  } catch (error) {
    console.error('[tag-pattern] 一覧の取得に失敗しました:', error)
    loadError.value = apiErrorMessage(error)
  } finally {
    loading.value = false
  }
}

// ---- 編集ドラフト ----

interface Draft {
  patternId: string | null
  name: string
  description: string
  isActive: boolean
  fields: TagPatternField[]
}

const draft = ref<Draft | null>(null)
const saving = ref(false)
const saveError = ref<ApiError | null>(null)

const VALUE_TYPES: ManualFieldValueType[] = ['string', 'number']
const COMPARE_MODES: ManualFieldCompareMode[] = ['text', 'ratio', 'careIcon']

function newField(): TagPatternField {
  return { key: '', label: '', valueType: 'string', multiple: false, compare: 'text', required: false }
}

function startCreate(): void {
  saveError.value = null
  draft.value = {
    patternId: null,
    name: '',
    description: '',
    isActive: true,
    fields: [newField()],
  }
}

function startEdit(pattern: TagPattern): void {
  saveError.value = null
  draft.value = {
    patternId: pattern.patternId,
    name: pattern.name,
    description: pattern.description,
    isActive: pattern.isActive,
    // ディープコピー（編集を保存まで一覧へ波及させない）。
    fields: pattern.fields.map((f) => ({ ...f })),
  }
}

function cancelEdit(): void {
  draft.value = null
  saveError.value = null
}

function addField(): void {
  draft.value?.fields.push(newField())
}

function removeField(index: number): void {
  draft.value?.fields.splice(index, 1)
}

/** 突合方式に応じて既定の単複を補正する（組成・洗濯表示は複数前提）。 */
function onCompareChange(field: TagPatternField): void {
  if (field.compare === 'ratio' || field.compare === 'careIcon') {
    field.multiple = true
  }
}

const canSave = computed(() => {
  const d = draft.value
  if (!d) return false
  if (d.name.trim() === '') return false
  if (d.fields.length === 0) return false
  return d.fields.every((f) => f.key.trim() !== '' && f.label.trim() !== '')
})

async function save(): Promise<void> {
  const d = draft.value
  if (!d || saving.value) return
  saveError.value = null
  saving.value = true
  try {
    const body: TagPatternUpsert = {
      name: d.name.trim(),
      description: d.description.trim(),
      isActive: d.isActive,
      fields: d.fields.map((f) => ({
        key: f.key.trim(),
        label: f.label.trim(),
        valueType: f.valueType,
        multiple: f.multiple,
        compare: f.compare,
        required: f.required,
      })),
    }
    if (d.patternId) {
      await put<TagPattern>(`/api/subsidiary-check/tag-patterns/${d.patternId}`, body)
    } else {
      await post<TagPattern>('/api/subsidiary-check/tag-patterns', body)
    }
    draft.value = null
    await loadPatterns()
  } catch (error) {
    console.error('[tag-pattern] 保存に失敗しました:', error)
    saveError.value =
      extractApiError(error) ?? { errorCode: 'UNKNOWN', summary: apiErrorMessage(error), remedy: '' }
  } finally {
    saving.value = false
  }
}

// ---- 削除 ----

const deletingId = ref<string | null>(null)
const deleteError = ref<string | null>(null)

async function remove(pattern: TagPattern): Promise<void> {
  if (deletingId.value) return
  if (!window.confirm(`タグパターン「${pattern.name}」を削除します。よろしいですか？\n（このパターンで実行済みの履歴は残ります）`)) {
    return
  }
  deletingId.value = pattern.patternId
  deleteError.value = null
  try {
    await del(`/api/subsidiary-check/tag-patterns/${pattern.patternId}`)
    if (draft.value?.patternId === pattern.patternId) draft.value = null
    await loadPatterns()
  } catch (error) {
    console.error('[tag-pattern] 削除に失敗しました:', error)
    deleteError.value = apiErrorMessage(error)
  } finally {
    deletingId.value = null
  }
}

onMounted(() => {
  void loadPatterns()
})
</script>

<template>
  <section aria-label="タグパターン設定" class="space-y-3">
    <div class="flex flex-wrap items-center justify-between gap-2">
      <p class="text-xs text-slate-500">
        手入力フォームの項目構成（項目・型・単複・突合方式）を管理します。
      </p>
      <div class="flex items-center gap-2">
        <button
          type="button"
          class="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-xs font-medium text-slate-600 hover:border-indigo-300 hover:text-indigo-700 disabled:opacity-40"
          :disabled="loading"
          @click="loadPatterns"
        >
          <RefreshCw class="h-3.5 w-3.5" :class="loading ? 'animate-spin' : ''" />
          更新
        </button>
        <button
          type="button"
          class="inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-indigo-700"
          @click="startCreate"
        >
          <Plus class="h-3.5 w-3.5" /> 新規パターン
        </button>
      </div>
    </div>

    <p v-if="deleteError" class="rounded-lg bg-rose-50 px-3 py-2 text-xs text-rose-700" role="alert">
      削除に失敗しました: {{ deleteError }}
    </p>

    <!-- 編集フォーム -->
    <div v-if="draft" class="rounded-xl border border-indigo-200 bg-white p-4 shadow-sm">
      <div class="flex items-center justify-between">
        <h3 class="text-sm font-bold text-slate-800">
          {{ draft.patternId ? 'パターンを編集' : '新規パターン' }}
        </h3>
        <button type="button" class="text-slate-400 hover:text-slate-700" aria-label="編集を閉じる" @click="cancelEdit">
          <X class="h-4 w-4" />
        </button>
      </div>

      <div class="mt-3 grid grid-cols-1 gap-3 sm:grid-cols-2">
        <div>
          <label class="text-xs font-medium text-slate-500">パターン名</label>
          <input
            v-model="draft.name"
            type="text"
            class="mt-1 w-full rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-400 focus:outline-none"
            :disabled="saving"
          >
        </div>
        <div>
          <label class="text-xs font-medium text-slate-500">説明（任意）</label>
          <input
            v-model="draft.description"
            type="text"
            class="mt-1 w-full rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-400 focus:outline-none"
            :disabled="saving"
          >
        </div>
      </div>

      <label class="mt-2 inline-flex items-center gap-2 text-xs text-slate-600">
        <input v-model="draft.isActive" type="checkbox" :disabled="saving"> 有効（新規チェックの選択肢に表示）
      </label>

      <!-- 項目エディタ -->
      <div class="mt-3">
        <div class="flex items-center justify-between">
          <h4 class="text-xs font-semibold text-slate-600">項目</h4>
          <button
            type="button"
            class="inline-flex items-center gap-1 text-xs font-medium text-indigo-600 hover:text-indigo-800 disabled:opacity-40"
            :disabled="saving || draft.fields.length >= MANUAL_MAX_FIELDS"
            @click="addField"
          >
            <Plus class="h-3.5 w-3.5" /> 項目を追加
          </button>
        </div>

        <div class="mt-2 overflow-x-auto">
          <table class="w-full min-w-[640px] text-xs">
            <thead>
              <tr class="text-left text-slate-400">
                <th class="px-1 py-1 font-medium">キー</th>
                <th class="px-1 py-1 font-medium">ラベル</th>
                <th class="px-1 py-1 font-medium">型</th>
                <th class="px-1 py-1 font-medium">複数</th>
                <th class="px-1 py-1 font-medium">突合方式</th>
                <th class="px-1 py-1 font-medium">必須</th>
                <th class="px-1 py-1" />
              </tr>
            </thead>
            <tbody>
              <tr v-for="(field, index) in draft.fields" :key="index" class="border-t border-slate-100">
                <td class="px-1 py-1">
                  <input
                    v-model="field.key"
                    type="text"
                    placeholder="productNumber"
                    class="w-28 rounded border border-slate-300 px-2 py-1 font-mono focus:border-indigo-400 focus:outline-none"
                    :disabled="saving"
                  >
                </td>
                <td class="px-1 py-1">
                  <input
                    v-model="field.label"
                    type="text"
                    placeholder="品番"
                    class="w-28 rounded border border-slate-300 px-2 py-1 focus:border-indigo-400 focus:outline-none"
                    :disabled="saving"
                  >
                </td>
                <td class="px-1 py-1">
                  <select
                    v-model="field.valueType"
                    class="rounded border border-slate-300 px-1 py-1 focus:border-indigo-400 focus:outline-none"
                    :disabled="saving"
                  >
                    <option v-for="t in VALUE_TYPES" :key="t" :value="t">{{ manualValueTypeLabel(t) }}</option>
                  </select>
                </td>
                <td class="px-1 py-1 text-center">
                  <input v-model="field.multiple" type="checkbox" :disabled="saving">
                </td>
                <td class="px-1 py-1">
                  <select
                    v-model="field.compare"
                    class="rounded border border-slate-300 px-1 py-1 focus:border-indigo-400 focus:outline-none"
                    :disabled="saving"
                    @change="onCompareChange(field)"
                  >
                    <option v-for="c in COMPARE_MODES" :key="c" :value="c">{{ manualCompareModeLabel(c) }}</option>
                  </select>
                </td>
                <td class="px-1 py-1 text-center">
                  <input v-model="field.required" type="checkbox" :disabled="saving">
                </td>
                <td class="px-1 py-1 text-right">
                  <button
                    type="button"
                    class="rounded p-1 text-slate-400 hover:text-rose-600 disabled:opacity-40"
                    :disabled="saving"
                    aria-label="項目を削除"
                    @click="removeField(index)"
                  >
                    <Trash2 class="h-3.5 w-3.5" />
                  </button>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <div v-if="saveError" class="mt-3 rounded-lg bg-red-50 p-3 text-sm text-red-700">
        <div class="flex items-start gap-2">
          <CircleX class="h-5 w-5 shrink-0" />
          <div>
            <p class="font-medium">[{{ saveError.errorCode }}] {{ saveError.summary }}</p>
            <p v-if="saveError.detail" class="text-xs text-red-600">{{ saveError.detail }}</p>
          </div>
        </div>
      </div>

      <div class="mt-3 flex items-center gap-2">
        <button
          type="button"
          class="inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700 disabled:opacity-50"
          :disabled="!canSave || saving"
          @click="save"
        >
          <LoaderCircle v-if="saving" class="h-4 w-4 animate-spin" />
          <Save v-else class="h-4 w-4" />
          {{ saving ? '保存中…' : '保存' }}
        </button>
        <button
          type="button"
          class="rounded-lg border border-slate-300 px-4 py-2 text-sm text-slate-600 hover:bg-slate-50"
          :disabled="saving"
          @click="cancelEdit"
        >
          キャンセル
        </button>
      </div>
    </div>

    <!-- 一覧 -->
    <StatusBlock
      :loading="loading"
      :error="loadError"
      :empty="!loading && !loadError && patterns.length === 0"
      empty-message="タグパターンがありません。「新規パターン」から作成してください。"
    >
      <ul class="space-y-2">
        <li
          v-for="pattern in patterns"
          :key="pattern.patternId"
          class="rounded-xl border border-slate-200 bg-white p-3 shadow-sm"
        >
          <div class="flex flex-wrap items-start justify-between gap-2">
            <div class="min-w-0">
              <div class="flex flex-wrap items-center gap-2">
                <span class="text-sm font-bold text-slate-800">{{ pattern.name }}</span>
                <span
                  v-if="!pattern.isActive"
                  class="rounded-full bg-slate-100 px-2 py-0.5 text-[10px] text-slate-500"
                >アーカイブ</span>
                <span class="text-[11px] text-slate-400">{{ pattern.fields.length }} 項目</span>
              </div>
              <p v-if="pattern.description" class="mt-0.5 text-xs text-slate-500">{{ pattern.description }}</p>
              <p class="mt-1 flex flex-wrap gap-1">
                <span
                  v-for="field in pattern.fields"
                  :key="field.key"
                  class="rounded bg-slate-100 px-1.5 py-0.5 text-[10px] text-slate-600"
                  :title="`${manualValueTypeLabel(field.valueType)} / ${manualCompareModeLabel(field.compare)}${field.multiple ? ' / 複数' : ''}${field.required ? ' / 必須' : ''}`"
                >{{ field.label || field.key }}</span>
              </p>
            </div>
            <div class="flex shrink-0 items-center gap-1">
              <button
                type="button"
                class="inline-flex items-center gap-1 rounded-lg border border-slate-300 px-2.5 py-1.5 text-xs text-slate-600 hover:bg-slate-50"
                @click="startEdit(pattern)"
              >
                <Pencil class="h-3.5 w-3.5" /> 編集
              </button>
              <button
                type="button"
                class="inline-flex items-center gap-1 rounded-lg border border-rose-200 px-2.5 py-1.5 text-xs text-rose-600 hover:bg-rose-50 disabled:opacity-40"
                :disabled="deletingId === pattern.patternId"
                @click="remove(pattern)"
              >
                <LoaderCircle v-if="deletingId === pattern.patternId" class="h-3.5 w-3.5 animate-spin" />
                <Trash2 v-else class="h-3.5 w-3.5" />
                削除
              </button>
            </div>
          </div>
        </li>
      </ul>
    </StatusBlock>
  </section>
</template>
