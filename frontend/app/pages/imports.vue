<script setup lang="ts">
import { Upload, FileDown, CircleCheck, CircleX } from 'lucide-vue-next'
import type { ApiError, ImportBatchInfo, ImportResult } from '~/types/api'

useHead({ title: '週次取込 | UndeuxSales' })

const { get, post } = useApi()

const batches = ref<ImportBatchInfo[]>([])
const loading = ref(true)
const errorMessage = ref<string | null>(null)

const fileInput = ref<HTMLInputElement | null>(null)
const selectedFile = ref<File | null>(null)
const uploading = ref(false)
const uploadResult = ref<ImportResult | null>(null)
const uploadError = ref<ApiError | null>(null)

// 週次CSVの必須列（バックエンド SalesCsvReader.RequiredColumns と一致させる）。
const requiredColumns = [
  'import_date', 'customer_code', 'gyotai_code', 'chohyo_kubun_name', 'department',
  'hinban_code', 'tanpin_code', 'hinmei', 'shohin_kigou', 'color', 'size',
  'toshu_uriage_count1', 'toshu_uriage_count2', 'toshu_uriage_count3',
  'toshu_uriage_count4', 'toshu_uriage_count5', 'toshu_uriage_count6',
  'toshu_uriage_count7', 'uriage_count_zenshu', 'uriage_count_2shumae',
  'uriage_count_3shumae', 'uriage_count_4shumae', 'zaikosu', 'ruikei_uriage_count',
  'ruikei_nohin_count', 'hatchu_count', 'donyu_date', 'zainiti', 'genka', 'baika',
  'kisetsu', 'sakizuke_count',
]

const statusLabels: Record<string, string> = {
  completed: '完了',
  processing: '処理中',
  failed: '失敗',
}
const sourceLabels: Record<string, string> = {
  initial_dump: '初期投入',
  weekly_csv: '週次CSV',
}

const columns = [
  { key: 'id', label: 'ID', format: (row: ImportBatchInfo) => String(row.id), frozen: true },
  {
    key: 'sourceType',
    label: '種別',
    format: (row: ImportBatchInfo) => sourceLabels[row.sourceType] ?? row.sourceType,
  },
  { key: 'fileName', label: 'ファイル名', format: (row: ImportBatchInfo) => row.fileName },
  {
    key: 'status',
    label: '状態',
    format: (row: ImportBatchInfo) => statusLabels[row.status] ?? row.status,
  },
  {
    key: 'rowCount',
    label: '行数',
    align: 'right' as const,
    format: (row: ImportBatchInfo) => formatNumber(row.rowCount),
  },
  {
    key: 'weekCount',
    label: '週数',
    align: 'right' as const,
    format: (row: ImportBatchInfo) => formatNumber(row.weekCount),
  },
  {
    key: 'range',
    label: '対象期間',
    format: (row: ImportBatchInfo) =>
      row.minImportDate && row.maxImportDate
        ? `${row.minImportDate} 〜 ${row.maxImportDate}`
        : '-',
  },
  {
    key: 'startedAt',
    label: '取込日時',
    format: (row: ImportBatchInfo) => formatDateTime(row.startedAt),
  },
]

async function loadHistory(): Promise<void> {
  loading.value = true
  errorMessage.value = null
  try {
    batches.value = await get<ImportBatchInfo[]>('/api/imports')
  } catch (error) {
    errorMessage.value = apiErrorMessage(error)
  } finally {
    loading.value = false
  }
}

function onFileChange(event: Event): void {
  const input = event.target as HTMLInputElement
  selectedFile.value = input.files?.[0] ?? null
  uploadResult.value = null
  uploadError.value = null
}

async function upload(): Promise<void> {
  if (!selectedFile.value) {
    return
  }
  uploading.value = true
  uploadResult.value = null
  uploadError.value = null
  try {
    const formData = new FormData()
    formData.append('file', selectedFile.value)
    uploadResult.value = await post<ImportResult>('/api/imports', formData)
    selectedFile.value = null
    if (fileInput.value) {
      fileInput.value.value = ''
    }
    await loadHistory()
  } catch (error) {
    uploadError.value =
      extractApiError(error) ??
      { errorCode: 'UNKNOWN', summary: apiErrorMessage(error), remedy: '' }
  } finally {
    uploading.value = false
  }
}

function downloadTemplate(): void {
  const header = [...requiredColumns, 'tanawari1', 'tanawari2'].join(',')
  const blob = new Blob([header + '\n'], { type: 'text/csv;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = 'weekly-sales-template.csv'
  link.click()
  URL.revokeObjectURL(url)
}

onMounted(loadHistory)
</script>

<template>
  <div class="space-y-4">
    <div>
      <h1 class="text-xl font-bold text-slate-800">週次取込</h1>
      <p class="text-sm text-slate-500">週次売上参照CSVのアップロードと取込履歴</p>
    </div>

    <div class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
      <div class="flex flex-wrap items-center gap-3">
        <input
          ref="fileInput"
          type="file"
          accept=".csv,text/csv"
          class="text-sm text-slate-600 file:mr-3 file:rounded-lg file:border-0 file:bg-slate-100 file:px-3 file:py-2 file:text-sm file:font-medium"
          @change="onFileChange"
        >
        <button
          type="button"
          :disabled="!selectedFile || uploading"
          class="flex items-center gap-1.5 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700 disabled:opacity-50"
          @click="upload"
        >
          <Upload class="h-4 w-4" />
          {{ uploading ? '取込中...' : '取込実行' }}
        </button>
        <button
          type="button"
          class="flex items-center gap-1.5 rounded-lg border border-slate-300 px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50"
          @click="downloadTemplate"
        >
          <FileDown class="h-4 w-4" />
          テンプレートCSV
        </button>
      </div>
      <p class="mt-2 text-xs text-slate-400">
        CSVは UTF-8。1行目にヘッダー（列名）が必要です。エラー行が1件でもある場合、取込は実行されません。
      </p>

      <div
        v-if="uploadResult"
        class="mt-3 flex items-start gap-2 rounded-lg bg-emerald-50 p-3 text-sm text-emerald-700"
      >
        <CircleCheck class="h-5 w-5 shrink-0" />
        <span>
          取込が完了しました（バッチ #{{ uploadResult.batchId }}、
          {{ formatNumber(uploadResult.rowCount) }} 行 /
          {{ formatNumber(uploadResult.weekCount) }} 週）。
        </span>
      </div>

      <div
        v-if="uploadError"
        class="mt-3 rounded-lg bg-red-50 p-3 text-sm text-red-700"
      >
        <div class="flex items-start gap-2">
          <CircleX class="h-5 w-5 shrink-0" />
          <div>
            <p class="font-medium">
              [{{ uploadError.errorCode }}] {{ uploadError.summary }}
            </p>
            <p v-if="uploadError.detail" class="text-xs text-red-600">
              {{ uploadError.detail }}
            </p>
            <p v-if="uploadError.remedy" class="text-xs text-red-500">
              {{ uploadError.remedy }}
            </p>
          </div>
        </div>
        <ul
          v-if="uploadError.details && uploadError.details.length > 0"
          class="mt-2 max-h-40 list-disc overflow-y-auto pl-8 text-xs"
        >
          <li v-for="(detail, index) in uploadError.details" :key="index">
            {{ detail }}
          </li>
        </ul>
      </div>
    </div>

    <h2 class="text-sm font-semibold text-slate-700">取込履歴</h2>
    <StatusBlock
      :loading="loading"
      :error="errorMessage"
      :empty="batches.length === 0"
      empty-message="取込履歴がありません。"
    >
      <DataTable
        :columns="columns"
        :rows="batches"
        :row-key="(row: ImportBatchInfo) => row.id"
      />
    </StatusBlock>
  </div>
</template>
