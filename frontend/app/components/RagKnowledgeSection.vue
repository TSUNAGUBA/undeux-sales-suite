<script setup lang="ts">
/**
 * RAG ナレッジの1スコープ（運営者 / ユーザー）ぶんの管理セクション。
 *
 * カテゴリタブ・業態フィルタ・検索・カード一覧（ページング）・登録/編集/削除/再インデックス/
 * 原本ダウンロードをまとめて提供する。運営者セクションを非管理者が開いた場合は
 * canEdit=false で閲覧のみ（変更系ボタン非表示）になる。
 */
import {
  ChevronLeft,
  ChevronRight,
  CircleCheck,
  CircleX,
  Download,
  Eye,
  FileText,
  LoaderCircle,
  Pencil,
  Plus,
  RefreshCw,
  Search,
  Trash2,
  Type,
  X,
} from 'lucide-vue-next'
import type {
  ApiError,
  ChatDomain,
  KnowledgeCreateTextRequest,
  KnowledgeDetail,
  KnowledgeItem,
  KnowledgeListResponse,
  KnowledgeUpdateRequest,
  OrgBusinessType,
  RagCategory,
  RagScope,
} from '~/types/api'

const props = defineProps<{
  scope: RagScope
  title: string
  description: string
  /** 変更系操作（登録・編集・削除・再インデックス）を許可するか。 */
  canEdit: boolean
  /** 業態フィルタ・分類フォームの選択肢に使う組織マスタ。 */
  businessTypes: OrgBusinessType[]
}>()

/** 登録・更新・削除で件数が変わった際に親（ステータスカード）へ通知する。 */
const emit = defineEmits<{ changed: [] }>()

const { get, post } = useApi()
const { getIdToken } = useAuth()
const config = useRuntimeConfig()
const apiBaseUrl = config.public.apiBaseUrl

const PAGE_SIZE = 12

// ---------------------------------------------------------------
// 一覧（カテゴリタブ・業態フィルタ・検索・ページング）
// ---------------------------------------------------------------
const categories = computed<RagCategory[]>(() => ragCategoriesForScope(props.scope))
const activeCategory = ref<RagCategory>('group')
const btFilter = ref<string | null>(null)
const searchInput = ref('')
const appliedSearch = ref('')
const page = ref(1)

const listData = ref<KnowledgeListResponse | null>(null)
const listLoading = ref(true)
const listError = ref<string | null>(null)

/** 業態フィルタを出すタブか（業態・部門カテゴリのみ）。 */
const showBtFilter = computed(
  () => activeCategory.value === 'business_type' || activeCategory.value === 'department',
)

const totalPages = computed(() => {
  const total = listData.value?.total ?? 0
  return total === 0 ? 1 : Math.ceil(total / PAGE_SIZE)
})

async function loadList(): Promise<void> {
  listLoading.value = true
  listError.value = null
  try {
    const query: Record<string, unknown> = {
      scope: props.scope,
      category: activeCategory.value,
      page: page.value,
      pageSize: PAGE_SIZE,
    }
    if (showBtFilter.value && btFilter.value) query.businessTypeCode = btFilter.value
    if (appliedSearch.value) query.search = appliedSearch.value
    listData.value = await get<KnowledgeListResponse>('/api/rag/knowledge', query)
  } catch (error) {
    listError.value = apiErrorMessage(error)
  } finally {
    listLoading.value = false
  }
}

function selectCategory(category: RagCategory): void {
  if (category === activeCategory.value) return
  activeCategory.value = category
  btFilter.value = null
  page.value = 1
  loadList()
}

function selectBtFilter(code: string | null): void {
  if (code === btFilter.value) return
  btFilter.value = code
  page.value = 1
  loadList()
}

function applySearch(): void {
  appliedSearch.value = searchInput.value.trim()
  page.value = 1
  loadList()
}

function changePage(delta: number): void {
  const next = page.value + delta
  if (next >= 1 && next <= totalPages.value) {
    page.value = next
    loadList()
  }
}

onMounted(loadList)

/** 業態コード → 表示名（未解決時はコードのまま）。 */
function businessTypeName(code: string | null): string | null {
  if (!code) return null
  return props.businessTypes.find((bt) => bt.code === code)?.displayName ?? code
}

// ---------------------------------------------------------------
// セクション内の操作結果通知（成功 / 失敗）
// ---------------------------------------------------------------
const notice = ref<string | null>(null)
const actionError = ref<string | null>(null)

function resetNotices(): void {
  notice.value = null
  actionError.value = null
}

// ---------------------------------------------------------------
// 詳細 / 編集 / 新規登録ダイアログ
// ---------------------------------------------------------------
type DialogMode = 'create' | 'edit' | 'view'

const dialogOpen = ref(false)
const dialogMode = ref<DialogMode>('view')
const dialogLoading = ref(false)
const dialogError = ref<ApiError | null>(null)
const dialogSubmitting = ref(false)
const detail = ref<KnowledgeDetail | null>(null)

/** 登録の入力方式（自由入力 / ファイル）。 */
const createMode = ref<'text' | 'file'>('text')
const createFile = ref<File | null>(null)
const fileInput = ref<HTMLInputElement | null>(null)

/** ダイアログの共通フォーム（'' は未選択 = null 相当）。 */
const form = reactive<{
  category: RagCategory
  businessTypeCode: string
  deptCode: string
  bizDomain: '' | ChatDomain
  title: string
  content: string
  description: string
}>({
  category: 'group',
  businessTypeCode: '',
  deptCode: '',
  bizDomain: '',
  title: '',
  content: '',
  description: '',
})

const needsBusinessType = computed(
  () => form.category === 'business_type' || form.category === 'department',
)
const needsDept = computed(() => form.category === 'department')

/** 分類フォームで選択中の業態配下の部門選択肢。 */
const formDeptOptions = computed(() => {
  const bt = props.businessTypes.find((b) => b.code === form.businessTypeCode)
  return bt ? [...bt.departments].sort((a, b) => a.displayOrder - b.displayOrder) : []
})

/** カテゴリ変更時に不要になった分類コードを明示的にリセットする（初期化時の watch 誤発火を避ける）。 */
function onFormCategoryChange(): void {
  if (!needsBusinessType.value) {
    form.businessTypeCode = ''
    form.deptCode = ''
  } else if (!needsDept.value) {
    form.deptCode = ''
  }
}

function onFormBusinessTypeChange(): void {
  form.deptCode = ''
}

function openCreate(): void {
  resetNotices()
  dialogMode.value = 'create'
  dialogError.value = null
  detail.value = null
  createMode.value = 'text'
  createFile.value = null
  form.category = activeCategory.value
  form.businessTypeCode = btFilter.value ?? ''
  form.deptCode = ''
  form.bizDomain = ''
  form.title = ''
  form.content = ''
  form.description = ''
  dialogOpen.value = true
}

async function openEntry(item: KnowledgeItem): Promise<void> {
  resetNotices()
  dialogMode.value = props.canEdit ? 'edit' : 'view'
  dialogError.value = null
  detail.value = null
  dialogOpen.value = true
  dialogLoading.value = true
  try {
    const loaded = await get<KnowledgeDetail>(`/api/rag/knowledge/${item.id}`)
    detail.value = loaded
    form.category = loaded.category
    form.businessTypeCode = loaded.businessTypeCode ?? ''
    form.deptCode = loaded.deptCode ?? ''
    form.bizDomain = loaded.bizDomain ?? ''
    form.title = loaded.title
    form.content = loaded.content
    form.description = ''
  } catch (error) {
    dialogError.value =
      extractApiError(error) ?? { errorCode: 'UNKNOWN', summary: apiErrorMessage(error), remedy: '' }
  } finally {
    dialogLoading.value = false
  }
}

function closeDialog(): void {
  if (dialogSubmitting.value) return
  dialogOpen.value = false
}

function onFileChange(event: Event): void {
  const input = event.target as HTMLInputElement
  createFile.value = input.files?.[0] ?? null
}

/** クライアント側検証（カテゴリ別必須・拡張子・サイズ上限）。エラー文言 or null。 */
function validateForm(): string | null {
  if (needsBusinessType.value && !form.businessTypeCode) return '業態を選択してください。'
  if (needsDept.value && !form.deptCode) return '部門を選択してください。'
  if (dialogMode.value === 'create' && createMode.value === 'file') {
    if (!createFile.value) return 'ファイルを選択してください。'
    if (!isAllowedRagFileName(createFile.value.name)) {
      return `対応していないファイル形式です（${RAG_FILE_ACCEPT} のみ登録できます）。`
    }
    if (isRagImageFileName(createFile.value.name) && createFile.value.size > RAG_MAX_IMAGE_BYTES) {
      return `画像ファイルのサイズが上限（${formatFileSize(RAG_MAX_IMAGE_BYTES)}）を超えています。`
    }
    if (createFile.value.size > RAG_MAX_FILE_BYTES) {
      return `ファイルサイズが上限（${formatFileSize(RAG_MAX_FILE_BYTES)}）を超えています。`
    }
    return null
  }
  if (!form.title.trim()) return 'タイトルを入力してください。'
  if (dialogMode.value === 'create' && !form.content.trim()) return '内容を入力してください。'
  return null
}

async function submitDialog(): Promise<void> {
  // 編集モードで詳細の読込に失敗している場合は保存対象が無い（エラー表示のみ）。
  if (dialogMode.value === 'edit' && !detail.value) return
  const validationError = validateForm()
  if (validationError) {
    dialogError.value = { errorCode: 'UNDX-REQ-002', summary: validationError, remedy: '' }
    return
  }
  dialogError.value = null
  dialogSubmitting.value = true
  try {
    if (dialogMode.value === 'create') {
      if (createMode.value === 'file') {
        const formData = new FormData()
        formData.append('scope', props.scope)
        formData.append('category', form.category)
        if (needsBusinessType.value && form.businessTypeCode) {
          formData.append('businessTypeCode', form.businessTypeCode)
        }
        if (needsDept.value && form.deptCode) formData.append('deptCode', form.deptCode)
        if (form.bizDomain) formData.append('bizDomain', form.bizDomain)
        if (form.title.trim()) formData.append('title', form.title.trim())
        if (form.description.trim()) formData.append('description', form.description.trim())
        formData.append('file', createFile.value!)
        const created = await post<KnowledgeDetail>('/api/rag/knowledge', formData)
        notice.value = `「${created.title}」を登録しました。`
      } else {
        const body: KnowledgeCreateTextRequest = {
          scope: props.scope,
          category: form.category,
          businessTypeCode: needsBusinessType.value ? form.businessTypeCode : null,
          deptCode: needsDept.value ? form.deptCode : null,
          bizDomain: form.bizDomain === '' ? null : form.bizDomain,
          title: form.title.trim(),
          content: form.content,
        }
        const created = await post<KnowledgeDetail>('/api/rag/knowledge', body)
        notice.value = `「${created.title}」を登録しました。`
      }
      page.value = 1
    } else if (dialogMode.value === 'edit' && detail.value) {
      const body: KnowledgeUpdateRequest = {
        title: form.title.trim(),
        content: form.content,
        category: form.category,
        businessTypeCode: needsBusinessType.value ? form.businessTypeCode : null,
        deptCode: needsDept.value ? form.deptCode : null,
        bizDomain: form.bizDomain === '' ? null : form.bizDomain,
      }
      const updated = await post<KnowledgeDetail>(
        `/api/rag/knowledge/${detail.value.id}/update`,
        body,
      )
      notice.value = `「${updated.title}」を更新しました。`
    }
    dialogOpen.value = false
    await loadList()
    emit('changed')
  } catch (error) {
    dialogError.value =
      extractApiError(error) ?? { errorCode: 'UNKNOWN', summary: apiErrorMessage(error), remedy: '' }
  } finally {
    dialogSubmitting.value = false
  }
}

// ---------------------------------------------------------------
// 再インデックス / 削除 / 原本ダウンロード
// ---------------------------------------------------------------
const busyId = ref<string | null>(null)

async function reindex(item: KnowledgeItem): Promise<void> {
  resetNotices()
  busyId.value = item.id
  try {
    const updated = await post<KnowledgeDetail>(`/api/rag/knowledge/${item.id}/reindex`, {})
    const items = listData.value?.items
    const index = items?.findIndex((i) => i.id === item.id) ?? -1
    if (items && index >= 0) items.splice(index, 1, updated)
    notice.value = `「${updated.title}」を再インデックスしました。`
    emit('changed')
  } catch (error) {
    actionError.value = apiErrorMessage(error)
  } finally {
    busyId.value = null
  }
}

async function removeEntry(item: KnowledgeItem): Promise<void> {
  if (!window.confirm(`「${item.title}」を削除しますか？削除するとチャットの回答に使われなくなります。`)) {
    return
  }
  resetNotices()
  busyId.value = item.id
  try {
    await post(`/api/rag/knowledge/${item.id}/delete`, {})
    notice.value = `「${item.title}」を削除しました。`
    // 最終ページの最後の1件を消した場合に空ページへ取り残されないよう調整する。
    if ((listData.value?.items.length ?? 0) <= 1 && page.value > 1) page.value -= 1
    await loadList()
    emit('changed')
  } catch (error) {
    actionError.value = apiErrorMessage(error)
  } finally {
    busyId.value = null
  }
}

/**
 * 原本ファイルのダウンロード。認証（Bearer）が必要なため <a href> ではなく
 * fetch + Blob URL で取得する（週次取込のテンプレート DL と同じ Blob リンク方式）。
 */
async function downloadFile(item: KnowledgeItem): Promise<void> {
  resetNotices()
  busyId.value = item.id
  try {
    const token = await getIdToken()
    const response = await fetch(`${apiBaseUrl}/api/rag/knowledge/${item.id}/file`, {
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
    })
    if (!response.ok) {
      const data: unknown = await response.json().catch(() => null)
      actionError.value = apiErrorMessage({ status: response.status, data })
      return
    }
    const blob = await response.blob()
    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    link.download = item.fileName ?? item.title
    link.click()
    URL.revokeObjectURL(url)
  } catch (error) {
    actionError.value = apiErrorMessage(error)
  } finally {
    busyId.value = null
  }
}
</script>

<template>
  <section class="rounded-xl border border-slate-200 bg-white p-4 shadow-sm">
    <!-- セクションヘッダー -->
    <div class="flex flex-wrap items-center justify-between gap-2">
      <div class="flex items-center gap-2">
        <h2 class="text-base font-bold text-slate-800">{{ title }}</h2>
        <span
          v-if="!canEdit"
          class="inline-flex items-center gap-1 rounded-full border border-slate-200 bg-slate-100 px-2 py-0.5 text-xs text-slate-500"
        >
          <Eye class="h-3 w-3" />
          閲覧のみ
        </span>
      </div>
      <button
        v-if="canEdit"
        type="button"
        class="inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-3 py-2 text-sm font-medium text-white hover:bg-indigo-700"
        @click="openCreate"
      >
        <Plus class="h-4 w-4" />
        新規登録
      </button>
    </div>
    <p class="mt-0.5 text-xs text-slate-500">{{ description }}</p>

    <!-- カテゴリタブ chips -->
    <div class="mt-3 flex flex-wrap gap-1.5">
      <button
        v-for="category in categories"
        :key="category"
        type="button"
        class="rounded-full border px-3 py-1.5 text-sm font-medium transition-colors"
        :class="category === activeCategory
          ? 'border-indigo-600 bg-indigo-600 text-white'
          : 'border-slate-300 bg-white text-slate-600 hover:border-indigo-400 hover:text-indigo-600'"
        @click="selectCategory(category)"
      >
        {{ RAG_CATEGORY_LABELS[category] }}
      </button>
    </div>

    <!-- 業態フィルタ（業態・部門タブのみ） -->
    <div v-if="showBtFilter" class="mt-2 flex flex-wrap gap-1.5">
      <button
        type="button"
        class="rounded-full border px-2.5 py-1 text-xs font-medium transition-colors"
        :class="btFilter === null
          ? 'border-slate-600 bg-slate-600 text-white'
          : 'border-slate-300 bg-white text-slate-500 hover:border-slate-400'"
        @click="selectBtFilter(null)"
      >
        すべて
      </button>
      <button
        v-for="bt in businessTypes"
        :key="bt.code"
        type="button"
        class="rounded-full border px-2.5 py-1 text-xs font-medium transition-colors"
        :class="btFilter === bt.code
          ? 'border-slate-600 bg-slate-600 text-white'
          : 'border-slate-300 bg-white text-slate-500 hover:border-slate-400'"
        @click="selectBtFilter(bt.code)"
      >
        {{ bt.displayName ?? bt.code }}
      </button>
    </div>

    <!-- 検索 -->
    <div class="mt-2 flex items-center gap-2">
      <input
        v-model="searchInput"
        type="search"
        placeholder="タイトル・本文を検索..."
        class="w-full max-w-xs rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-sm focus:border-indigo-400 focus:outline-none"
        @keydown.enter.prevent="applySearch"
      >
      <button
        type="button"
        class="inline-flex shrink-0 items-center gap-1 rounded-lg border border-slate-300 px-3 py-1.5 text-sm text-slate-600 hover:bg-slate-50"
        @click="applySearch"
      >
        <Search class="h-3.5 w-3.5" />
        検索
      </button>
    </div>

    <!-- 操作結果の通知 -->
    <p
      v-if="notice"
      class="mt-3 flex items-center gap-1.5 rounded-lg bg-emerald-50 px-3 py-2 text-sm text-emerald-700"
    >
      <CircleCheck class="h-4 w-4 shrink-0" />
      {{ notice }}
    </p>
    <p
      v-if="actionError"
      class="mt-3 flex items-center gap-1.5 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700"
    >
      <CircleX class="h-4 w-4 shrink-0" />
      {{ actionError }}
    </p>

    <!-- 一覧 -->
    <div class="mt-3">
      <StatusBlock
        :loading="listLoading"
        :error="listError"
        :empty="(listData?.items.length ?? 0) === 0"
        empty-message="このカテゴリにはナレッジが登録されていません。"
      >
        <div class="space-y-3">
          <p class="text-xs text-slate-500">全 {{ formatNumber(listData?.total ?? 0) }} 件</p>
          <div class="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-3">
            <article
              v-for="item in listData?.items ?? []"
              :key="item.id"
              class="flex flex-col gap-2 rounded-xl border border-slate-200 bg-white p-3 shadow-sm"
            >
              <div class="flex items-start justify-between gap-2">
                <h3 class="min-w-0 flex-1 truncate text-sm font-bold text-slate-800" :title="item.title">
                  {{ item.title }}
                </h3>
                <component
                  :is="item.sourceType === 'file' ? FileText : Type"
                  class="h-4 w-4 shrink-0 text-slate-400"
                  :aria-label="item.sourceType === 'file' ? 'ファイル' : '自由入力'"
                />
              </div>

              <div class="flex flex-wrap items-center gap-1">
                <span class="rounded-full border border-indigo-200 bg-indigo-50 px-2 py-0.5 text-[11px] text-indigo-700">
                  {{ RAG_CATEGORY_LABELS[item.category] }}
                </span>
                <span
                  v-if="item.businessTypeCode"
                  class="rounded-full border border-violet-200 bg-violet-50 px-2 py-0.5 text-[11px] text-violet-700"
                >
                  {{ businessTypeName(item.businessTypeCode) }}<template v-if="item.deptCode"> / {{ item.deptCode }}</template>
                </span>
                <span
                  v-if="item.bizDomain"
                  class="rounded-full border border-sky-200 bg-sky-50 px-2 py-0.5 text-[11px] text-sky-700"
                >
                  {{ CHAT_DOMAIN_LABELS[item.bizDomain] }}
                </span>
                <span
                  class="rounded-full border px-2 py-0.5 text-[11px]"
                  :class="RAG_INDEX_STATE_META[item.indexState].badgeClass"
                  :title="item.indexState === 'failed' ? item.indexError ?? undefined : undefined"
                >
                  {{ RAG_INDEX_STATE_META[item.indexState].label }}
                </span>
                <span
                  v-if="item.isSeed"
                  class="rounded-full border border-slate-200 bg-slate-50 px-2 py-0.5 text-[11px] text-slate-500"
                >
                  初期データ
                </span>
              </div>

              <p class="line-clamp-2 text-xs leading-relaxed text-slate-500">{{ item.contentPreview }}</p>
              <p
                v-if="item.indexState === 'failed' && item.indexError"
                class="line-clamp-2 text-xs text-red-600"
                :title="item.indexError"
              >
                {{ item.indexError }}
              </p>
              <p v-if="item.sourceType === 'file'" class="truncate text-[11px] text-slate-400" :title="item.fileName ?? undefined">
                {{ item.fileName }}（{{ formatFileSize(item.fileSizeBytes) }}）
              </p>

              <div class="mt-auto flex flex-wrap items-center justify-between gap-1.5 border-t border-slate-100 pt-2">
                <span class="text-[11px] text-slate-400">
                  {{ formatNumber(item.chunkCount) }}チャンク・{{ formatDateTime(item.updatedAt) }}
                </span>
                <div class="flex flex-wrap items-center gap-1">
                  <button
                    type="button"
                    class="inline-flex items-center gap-1 rounded-md px-1.5 py-1 text-xs text-indigo-600 hover:bg-indigo-50"
                    @click="openEntry(item)"
                  >
                    <component :is="canEdit ? Pencil : Eye" class="h-3.5 w-3.5" />
                    {{ canEdit ? '詳細/編集' : '詳細' }}
                  </button>
                  <button
                    v-if="canEdit"
                    type="button"
                    :disabled="busyId === item.id"
                    class="inline-flex items-center gap-1 rounded-md px-1.5 py-1 text-xs text-slate-600 hover:bg-slate-100 disabled:opacity-50"
                    @click="reindex(item)"
                  >
                    <RefreshCw class="h-3.5 w-3.5" :class="busyId === item.id ? 'animate-spin' : ''" />
                    再インデックス
                  </button>
                  <button
                    v-if="item.sourceType === 'file'"
                    type="button"
                    :disabled="busyId === item.id"
                    class="inline-flex items-center gap-1 rounded-md px-1.5 py-1 text-xs text-slate-600 hover:bg-slate-100 disabled:opacity-50"
                    @click="downloadFile(item)"
                  >
                    <Download class="h-3.5 w-3.5" />
                    原本
                  </button>
                  <button
                    v-if="canEdit"
                    type="button"
                    :disabled="busyId === item.id"
                    class="inline-flex items-center gap-1 rounded-md px-1.5 py-1 text-xs text-rose-600 hover:bg-rose-50 disabled:opacity-50"
                    @click="removeEntry(item)"
                  >
                    <Trash2 class="h-3.5 w-3.5" />
                    削除
                  </button>
                </div>
              </div>
            </article>
          </div>

          <!-- ページング -->
          <div class="flex items-center justify-between text-sm text-slate-600">
            <span>{{ page }} / {{ totalPages }} ページ</span>
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

    <!-- 詳細 / 編集 / 新規登録ダイアログ（モバイルはボトムシート風） -->
    <div
      v-if="dialogOpen"
      class="fixed inset-0 z-40 flex items-end justify-center bg-slate-900/40 sm:items-center sm:p-4"
      @click.self="closeDialog"
    >
      <div class="max-h-[92vh] w-full overflow-y-auto rounded-t-xl bg-white p-4 shadow-xl sm:max-w-2xl sm:rounded-xl sm:p-5">
        <div class="mb-3 flex items-center justify-between gap-2">
          <h3 class="text-base font-bold text-slate-800">
            {{ dialogMode === 'create' ? 'ナレッジの新規登録' : dialogMode === 'edit' ? 'ナレッジの詳細・編集' : 'ナレッジの詳細' }}
          </h3>
          <button
            type="button"
            class="rounded-md p-1 text-slate-400 hover:bg-slate-100 hover:text-slate-600"
            aria-label="閉じる"
            @click="closeDialog"
          >
            <X class="h-5 w-5" />
          </button>
        </div>

        <div v-if="dialogLoading" class="flex items-center justify-center gap-2 py-10 text-sm text-slate-500">
          <LoaderCircle class="h-5 w-5 animate-spin text-indigo-500" />
          読み込み中...
        </div>

        <template v-else>
          <!-- 閲覧のみ -->
          <div v-if="dialogMode === 'view' && detail" class="space-y-3">
            <div>
              <p class="text-xs font-medium text-slate-500">タイトル</p>
              <p class="text-sm font-medium text-slate-800">{{ detail.title }}</p>
            </div>
            <div class="flex flex-wrap gap-1">
              <span class="rounded-full border border-indigo-200 bg-indigo-50 px-2 py-0.5 text-[11px] text-indigo-700">
                {{ RAG_CATEGORY_LABELS[detail.category] }}
              </span>
              <span
                v-if="detail.businessTypeCode"
                class="rounded-full border border-violet-200 bg-violet-50 px-2 py-0.5 text-[11px] text-violet-700"
              >
                {{ businessTypeName(detail.businessTypeCode) }}<template v-if="detail.deptCode"> / {{ detail.deptCode }}</template>
              </span>
              <span class="rounded-full border border-sky-200 bg-sky-50 px-2 py-0.5 text-[11px] text-sky-700">
                {{ chatDomainLabel(detail.bizDomain) }}
              </span>
            </div>
            <div>
              <p class="mb-1 text-xs font-medium text-slate-500">内容</p>
              <p class="max-h-72 overflow-y-auto whitespace-pre-wrap rounded-lg bg-slate-50 p-3 text-sm leading-relaxed text-slate-700">{{ detail.content }}</p>
            </div>
            <button
              v-if="detail.sourceType === 'file'"
              type="button"
              class="inline-flex items-center gap-1.5 rounded-lg border border-slate-300 px-3 py-2 text-sm text-slate-600 hover:bg-slate-50"
              @click="downloadFile(detail)"
            >
              <Download class="h-4 w-4" />
              原本をダウンロード（{{ detail.fileName }}）
            </button>
          </div>

          <!-- 登録 / 編集フォーム -->
          <form v-else class="space-y-3" @submit.prevent="submitDialog">
            <!-- 入力方式（新規のみ） -->
            <div v-if="dialogMode === 'create'" class="flex gap-1.5">
              <button
                type="button"
                class="inline-flex items-center gap-1.5 rounded-lg border px-3 py-1.5 text-sm font-medium"
                :class="createMode === 'text'
                  ? 'border-indigo-600 bg-indigo-50 text-indigo-700'
                  : 'border-slate-300 bg-white text-slate-500 hover:border-slate-400'"
                @click="createMode = 'text'"
              >
                <Type class="h-4 w-4" />
                自由入力
              </button>
              <button
                type="button"
                class="inline-flex items-center gap-1.5 rounded-lg border px-3 py-1.5 text-sm font-medium"
                :class="createMode === 'file'
                  ? 'border-indigo-600 bg-indigo-50 text-indigo-700'
                  : 'border-slate-300 bg-white text-slate-500 hover:border-slate-400'"
                @click="createMode = 'file'"
              >
                <FileText class="h-4 w-4" />
                ファイル
              </button>
            </div>

            <!-- 分類 -->
            <div class="grid grid-cols-1 gap-3 sm:grid-cols-2">
              <div>
                <label class="mb-1 block text-xs font-medium text-slate-500">カテゴリ</label>
                <select
                  v-model="form.category"
                  class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
                  @change="onFormCategoryChange"
                >
                  <option v-for="category in categories" :key="category" :value="category">
                    {{ RAG_CATEGORY_LABELS[category] }}
                  </option>
                </select>
              </div>
              <div>
                <label class="mb-1 block text-xs font-medium text-slate-500">対象部署（業務チャットの絞込）</label>
                <select v-model="form.bizDomain" class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm">
                  <option value="">共通（絞込なし）</option>
                  <option v-for="domain in CHAT_DOMAINS" :key="domain" :value="domain">
                    {{ CHAT_DOMAIN_LABELS[domain] }}
                  </option>
                </select>
              </div>
              <div v-if="needsBusinessType">
                <label class="mb-1 block text-xs font-medium text-slate-500">業態（必須）</label>
                <select
                  v-model="form.businessTypeCode"
                  class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
                  @change="onFormBusinessTypeChange"
                >
                  <option value="" disabled>選択してください</option>
                  <option v-for="bt in businessTypes" :key="bt.code" :value="bt.code">
                    {{ bt.displayName ?? bt.code }}
                  </option>
                </select>
              </div>
              <div v-if="needsDept">
                <label class="mb-1 block text-xs font-medium text-slate-500">部門（必須）</label>
                <select v-model="form.deptCode" class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm">
                  <option value="" disabled>選択してください</option>
                  <option v-for="dept in formDeptOptions" :key="dept.id" :value="dept.deptCode">
                    {{ dept.deptCode }} {{ dept.deptName }}
                  </option>
                </select>
              </div>
            </div>

            <!-- タイトル -->
            <div>
              <label class="mb-1 block text-xs font-medium text-slate-500">
                タイトル<template v-if="dialogMode === 'create' && createMode === 'file'">（省略時はファイル名）</template>
              </label>
              <input
                v-model="form.title"
                type="text"
                class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm"
                placeholder="例: 納品リードタイムの基本ルール"
              >
            </div>

            <!-- 本文（自由入力・編集） -->
            <div v-if="dialogMode === 'edit' || createMode === 'text'">
              <label class="mb-1 block text-xs font-medium text-slate-500">
                内容
                <template v-if="dialogMode === 'edit' && detail?.sourceType === 'file'">
                  （ファイルからの抽出テキスト。編集して保存すると再インデックスされます。原本ファイルは変わりません）
                </template>
              </label>
              <textarea
                v-model="form.content"
                rows="8"
                class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm leading-relaxed"
                placeholder="チャットの回答に使う知識を入力してください。"
              />
            </div>

            <!-- ファイル（新規・ファイルモード） -->
            <template v-if="dialogMode === 'create' && createMode === 'file'">
              <div>
                <label class="mb-1 block text-xs font-medium text-slate-500">
                  ファイル（{{ RAG_FILE_ACCEPT }}・最大 {{ formatFileSize(RAG_MAX_FILE_BYTES) }}、画像は {{ formatFileSize(RAG_MAX_IMAGE_BYTES) }}）
                </label>
                <input
                  ref="fileInput"
                  type="file"
                  :accept="RAG_FILE_ACCEPT"
                  class="w-full text-sm text-slate-600 file:mr-3 file:rounded-lg file:border-0 file:bg-slate-100 file:px-3 file:py-2 file:text-sm file:font-medium"
                  @change="onFileChange"
                >
              </div>
              <div>
                <label class="mb-1 block text-xs font-medium text-slate-500">説明（任意。画像などの補足。検索対象に含まれます）</label>
                <textarea
                  v-model="form.description"
                  rows="3"
                  class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm leading-relaxed"
                  placeholder="例: 物流センターの入荷検品フローを図解した資料"
                />
              </div>
            </template>

            <!-- 編集時のファイル情報 -->
            <div
              v-if="dialogMode === 'edit' && detail?.sourceType === 'file'"
              class="flex flex-wrap items-center justify-between gap-2 rounded-lg bg-slate-50 px-3 py-2 text-xs text-slate-500"
            >
              <span class="min-w-0 truncate" :title="detail.fileName ?? undefined">
                原本: {{ detail.fileName }}（{{ formatFileSize(detail.fileSizeBytes) }}）
              </span>
              <button
                type="button"
                class="inline-flex shrink-0 items-center gap-1 rounded-md px-1.5 py-1 text-xs text-indigo-600 hover:bg-indigo-50"
                @click="detail && downloadFile(detail)"
              >
                <Download class="h-3.5 w-3.5" />
                ダウンロード
              </button>
            </div>

            <!-- ダイアログ内エラー -->
            <div v-if="dialogError" class="rounded-lg bg-red-50 p-3 text-sm text-red-700">
              <p class="font-medium">[{{ dialogError.errorCode }}] {{ dialogError.summary }}</p>
              <p v-if="dialogError.detail" class="text-xs text-red-600">{{ dialogError.detail }}</p>
              <p v-if="dialogError.remedy" class="text-xs text-red-500">{{ dialogError.remedy }}</p>
              <ul
                v-if="dialogError.details && dialogError.details.length > 0"
                class="mt-1 list-disc pl-5 text-xs"
              >
                <li v-for="(detailText, index) in dialogError.details" :key="index">{{ detailText }}</li>
              </ul>
            </div>

            <div class="flex items-center justify-end gap-2 pt-1">
              <button
                type="button"
                class="rounded-lg border border-slate-300 px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50"
                :disabled="dialogSubmitting"
                @click="closeDialog"
              >
                キャンセル
              </button>
              <button
                type="submit"
                class="inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700 disabled:opacity-50"
                :disabled="dialogSubmitting || (dialogMode === 'edit' && !detail)"
              >
                <LoaderCircle v-if="dialogSubmitting" class="h-4 w-4 animate-spin" />
                {{ dialogMode === 'create' ? '登録' : '保存' }}
              </button>
            </div>
          </form>

          <!-- 閲覧モードでの読み込みエラー -->
          <div
            v-if="dialogMode === 'view' && !detail && dialogError"
            class="rounded-lg bg-red-50 p-3 text-sm text-red-700"
          >
            <p class="font-medium">[{{ dialogError.errorCode }}] {{ dialogError.summary }}</p>
            <p v-if="dialogError.detail" class="text-xs text-red-600">{{ dialogError.detail }}</p>
            <p v-if="dialogError.remedy" class="text-xs text-red-500">{{ dialogError.remedy }}</p>
          </div>
        </template>
      </div>
    </div>
  </section>
</template>
