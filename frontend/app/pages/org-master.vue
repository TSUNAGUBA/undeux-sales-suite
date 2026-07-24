<script setup lang="ts">
/**
 * 業態・部門マスタ（/org-master）。
 *
 * 業態 chips → 選択業態の部署（商品部）一覧・部門一覧を表示する。商談チャットの選択肢と
 * 相談受付デスクの SoT（バックエンド DB）を閲覧・整備する画面。
 * 編集（追加・編集・削除）は管理者（isOwner）のみ。非管理者は閲覧のみ。
 */
import { Building2, CircleX, Eye, Headset, LoaderCircle, Pencil, Plus, Trash2, X } from 'lucide-vue-next'
import type {
  ApiError,
  ChatDomain,
  ContactDesk,
  ContactDeskSaveRequest,
  ContactDesksResponse,
  OrgBusinessType,
  OrgDepartment,
  OrgDepartmentSaveRequest,
  OrgMasterResponse,
  OrgSection,
  OrgSectionSaveRequest,
} from '~/types/api'

useHead({ title: '業態・部門マスタ | UndeuxSales' })

const { get, post } = useApi()
const { isOwner } = useAccountType()

// ---------------------------------------------------------------
// データロード
// ---------------------------------------------------------------
const businessTypes = ref<OrgBusinessType[]>([])
const desks = ref<ContactDesk[]>([])
const loading = ref(true)
const errorMessage = ref<string | null>(null)
const actionError = ref<string | null>(null)

const selectedBtCode = ref<string | null>(null)

async function loadOrg(): Promise<void> {
  const response = await get<OrgMasterResponse>('/api/org-master')
  businessTypes.value = response.businessTypes
  // 選択中の業態を維持する（初回・削除等で不在の場合は先頭へフォールバック）。
  if (!businessTypes.value.some((bt) => bt.code === selectedBtCode.value)) {
    selectedBtCode.value = businessTypes.value[0]?.code ?? null
  }
}

async function loadDesks(): Promise<void> {
  const response = await get<ContactDesksResponse>('/api/org-master/contact-desks')
  desks.value = response.desks
}

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = null
  try {
    await Promise.all([loadOrg(), loadDesks()])
  } catch (error) {
    errorMessage.value = apiErrorMessage(error)
  } finally {
    loading.value = false
  }
}

onMounted(load)

const selectedBusinessType = computed(
  () => businessTypes.value.find((bt) => bt.code === selectedBtCode.value) ?? null,
)

const sections = computed<OrgSection[]>(() =>
  [...(selectedBusinessType.value?.sections ?? [])].sort((a, b) => a.displayOrder - b.displayOrder),
)
const departments = computed<OrgDepartment[]>(() =>
  [...(selectedBusinessType.value?.departments ?? [])].sort(
    (a, b) => a.displayOrder - b.displayOrder,
  ),
)
const sortedDesks = computed<ContactDesk[]>(() =>
  [...desks.value].sort((a, b) => a.displayOrder - b.displayOrder),
)

function sectionNameById(id: number | null): string {
  if (id === null) return '未設定'
  const section = selectedBusinessType.value?.sections.find((s) => s.id === id)
  return section ? section.name : '未設定'
}

// ---------------------------------------------------------------
// テーブル列定義（操作列は管理者のみ）
// ---------------------------------------------------------------
const sectionColumns = computed(() => {
  const columns = [
    { key: 'name', label: '部署名', format: (row: OrgSection) => row.name },
    { key: 'categoryNote', label: '取扱分類', format: (row: OrgSection) => row.categoryNote ?? '-' },
    { key: 'phone', label: '電話', format: (row: OrgSection) => row.phone ?? '-' },
    { key: 'floor', label: 'フロア', format: (row: OrgSection) => row.floor ?? '-' },
    {
      key: 'displayOrder',
      label: '表示順',
      align: 'right' as const,
      format: (row: OrgSection) => String(row.displayOrder),
    },
  ]
  if (isOwner.value) {
    columns.push({ key: 'actions', label: '操作', align: 'right' as const, format: () => '' })
  }
  return columns
})

const departmentColumns = computed(() => {
  const columns = [
    { key: 'deptCode', label: '部門CD', format: (row: OrgDepartment) => row.deptCode },
    { key: 'deptName', label: '部門名', format: (row: OrgDepartment) => row.deptName },
    {
      key: 'buyerSectionId',
      label: '担当商品部',
      format: (row: OrgDepartment) => sectionNameById(row.buyerSectionId),
    },
    {
      key: 'displayOrder',
      label: '表示順',
      align: 'right' as const,
      format: (row: OrgDepartment) => String(row.displayOrder),
    },
  ]
  if (isOwner.value) {
    columns.push({ key: 'actions', label: '操作', align: 'right' as const, format: () => '' })
  }
  return columns
})

const deskColumns = computed(() => {
  const columns = [
    { key: 'topic', label: '相談内容', format: (row: ContactDesk) => row.topic },
    { key: 'departmentName', label: '担当部署', format: (row: ContactDesk) => row.departmentName },
    { key: 'phone', label: '電話', format: (row: ContactDesk) => row.phone ?? '-' },
    {
      key: 'chatDomain',
      label: 'チャット窓口',
      format: (row: ContactDesk) => (row.chatDomain ? CHAT_DOMAIN_LABELS[row.chatDomain] : '-'),
    },
    {
      key: 'displayOrder',
      label: '表示順',
      align: 'right' as const,
      format: (row: ContactDesk) => String(row.displayOrder),
    },
  ]
  if (isOwner.value) {
    columns.push({ key: 'actions', label: '操作', align: 'right' as const, format: () => '' })
  }
  return columns
})

// ---------------------------------------------------------------
// 編集ダイアログ（部署 / 部門 / 相談受付デスク）
// ---------------------------------------------------------------
type EditorKind = 'section' | 'department' | 'desk'

const editorOpen = ref(false)
const editorKind = ref<EditorKind>('section')
const editorError = ref<ApiError | null>(null)
const editorSaving = ref(false)

const editorTitles: Record<EditorKind, { create: string; edit: string }> = {
  section: { create: '部署（商品部）の追加', edit: '部署（商品部）の編集' },
  department: { create: '部門の追加', edit: '部門の編集' },
  desk: { create: '相談受付デスクの追加', edit: '相談受付デスクの編集' },
}

const sectionForm = reactive({
  id: null as number | null,
  name: '',
  categoryNote: '',
  phone: '',
  floor: '',
  displayOrder: 1,
})
const deptForm = reactive({
  id: null as number | null,
  deptCode: '',
  deptName: '',
  buyerSectionId: null as number | null,
  displayOrder: 1,
})
const deskForm = reactive({
  id: null as number | null,
  topic: '',
  departmentName: '',
  phone: '',
  chatDomain: '' as '' | ChatDomain,
  displayOrder: 1,
})

const editorIsCreate = computed(() => {
  if (editorKind.value === 'section') return sectionForm.id === null
  if (editorKind.value === 'department') return deptForm.id === null
  return deskForm.id === null
})

function nextDisplayOrder(rows: { displayOrder: number }[]): number {
  return rows.reduce((max, row) => Math.max(max, row.displayOrder), 0) + 1
}

function openSectionEditor(section: OrgSection | null): void {
  sectionForm.id = section?.id ?? null
  sectionForm.name = section?.name ?? ''
  sectionForm.categoryNote = section?.categoryNote ?? ''
  sectionForm.phone = section?.phone ?? ''
  sectionForm.floor = section?.floor ?? ''
  sectionForm.displayOrder = section?.displayOrder ?? nextDisplayOrder(sections.value)
  editorKind.value = 'section'
  editorError.value = null
  editorOpen.value = true
}

function openDepartmentEditor(dept: OrgDepartment | null): void {
  deptForm.id = dept?.id ?? null
  deptForm.deptCode = dept?.deptCode ?? ''
  deptForm.deptName = dept?.deptName ?? ''
  deptForm.buyerSectionId = dept?.buyerSectionId ?? null
  deptForm.displayOrder = dept?.displayOrder ?? nextDisplayOrder(departments.value)
  editorKind.value = 'department'
  editorError.value = null
  editorOpen.value = true
}

function openDeskEditor(desk: ContactDesk | null): void {
  deskForm.id = desk?.id ?? null
  deskForm.topic = desk?.topic ?? ''
  deskForm.departmentName = desk?.departmentName ?? ''
  deskForm.phone = desk?.phone ?? ''
  deskForm.chatDomain = desk?.chatDomain ?? ''
  deskForm.displayOrder = desk?.displayOrder ?? nextDisplayOrder(sortedDesks.value)
  editorKind.value = 'desk'
  editorError.value = null
  editorOpen.value = true
}

function closeEditor(): void {
  if (editorSaving.value) return
  editorOpen.value = false
}

/** 空文字を null へ正規化する（契約の nullable フィールド用）。 */
function emptyToNull(value: string): string | null {
  const trimmed = value.trim()
  return trimmed === '' ? null : trimmed
}

function validateEditor(): string | null {
  if (editorKind.value === 'section') {
    if (!sectionForm.name.trim()) return '部署名を入力してください。'
  } else if (editorKind.value === 'department') {
    if (!deptForm.deptCode.trim()) return '部門CDを入力してください。'
    if (!deptForm.deptName.trim()) return '部門名を入力してください。'
  } else {
    if (!deskForm.topic.trim()) return '相談内容を入力してください。'
    if (!deskForm.departmentName.trim()) return '担当部署を入力してください。'
  }
  return null
}

async function submitEditor(): Promise<void> {
  const bt = selectedBusinessType.value
  const validationError = validateEditor()
  if (validationError) {
    editorError.value = { errorCode: 'UNDX-REQ-002', summary: validationError, remedy: '' }
    return
  }
  editorError.value = null
  editorSaving.value = true
  try {
    if (editorKind.value === 'section') {
      if (!bt) return
      const body: OrgSectionSaveRequest = {
        id: sectionForm.id,
        businessTypeCode: bt.code,
        name: sectionForm.name.trim(),
        categoryNote: emptyToNull(sectionForm.categoryNote),
        phone: emptyToNull(sectionForm.phone),
        floor: emptyToNull(sectionForm.floor),
        displayOrder: sectionForm.displayOrder,
      }
      await post<OrgSection>('/api/org-master/sections/save', body)
      await loadOrg()
    } else if (editorKind.value === 'department') {
      if (!bt) return
      const body: OrgDepartmentSaveRequest = {
        id: deptForm.id,
        businessTypeCode: bt.code,
        deptCode: deptForm.deptCode.trim(),
        deptName: deptForm.deptName.trim(),
        buyerSectionId: deptForm.buyerSectionId,
        displayOrder: deptForm.displayOrder,
      }
      await post<OrgDepartment>('/api/org-master/departments/save', body)
      await loadOrg()
    } else {
      const body: ContactDeskSaveRequest = {
        id: deskForm.id,
        topic: deskForm.topic.trim(),
        departmentName: deskForm.departmentName.trim(),
        phone: emptyToNull(deskForm.phone),
        chatDomain: deskForm.chatDomain === '' ? null : deskForm.chatDomain,
        displayOrder: deskForm.displayOrder,
      }
      await post<ContactDesk>('/api/org-master/contact-desks/save', body)
      await loadDesks()
    }
    editorOpen.value = false
  } catch (error) {
    editorError.value =
      extractApiError(error) ?? { errorCode: 'UNKNOWN', summary: apiErrorMessage(error), remedy: '' }
  } finally {
    editorSaving.value = false
  }
}

// ---------------------------------------------------------------
// 削除
// ---------------------------------------------------------------
async function deleteSection(section: OrgSection): Promise<void> {
  if (!window.confirm(`部署「${section.name}」を削除しますか？配下の部門は「部署未設定」として残ります。`)) {
    return
  }
  actionError.value = null
  try {
    await post('/api/org-master/sections/delete', { id: section.id })
    await loadOrg()
  } catch (error) {
    actionError.value = apiErrorMessage(error)
  }
}

async function deleteDepartment(dept: OrgDepartment): Promise<void> {
  if (!window.confirm(`部門「${dept.deptCode} ${dept.deptName}」を削除しますか？`)) return
  actionError.value = null
  try {
    await post('/api/org-master/departments/delete', { id: dept.id })
    await loadOrg()
  } catch (error) {
    actionError.value = apiErrorMessage(error)
  }
}

async function deleteDesk(desk: ContactDesk): Promise<void> {
  if (!window.confirm(`相談受付デスク「${desk.topic}」を削除しますか？`)) return
  actionError.value = null
  try {
    await post('/api/org-master/contact-desks/delete', { id: desk.id })
    await loadDesks()
  } catch (error) {
    actionError.value = apiErrorMessage(error)
  }
}
</script>

<template>
  <div class="space-y-4">
    <div class="flex flex-wrap items-center justify-between gap-2">
      <div>
        <h1 class="text-xl font-bold text-slate-800">業態・部門マスタ</h1>
        <p class="text-sm text-slate-500">
          業態ごとの部署（商品部）・部門と、相談受付デスクを管理します。商談チャットの選択肢の元データです。
        </p>
      </div>
      <span
        v-if="!isOwner"
        class="inline-flex items-center gap-1 rounded-full border border-slate-200 bg-slate-100 px-2.5 py-1 text-xs text-slate-500"
      >
        <Eye class="h-3.5 w-3.5" />
        閲覧のみ（編集は管理者のみ）
      </span>
    </div>

    <StatusBlock
      :loading="loading"
      :error="errorMessage"
      :empty="businessTypes.length === 0"
      empty-message="業態マスタが登録されていません。"
    >
      <div class="space-y-4">
        <p
          v-if="actionError"
          class="flex items-center gap-1.5 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700"
        >
          <CircleX class="h-4 w-4 shrink-0" />
          {{ actionError }}
        </p>

        <!-- 業態 chips -->
        <div class="flex flex-wrap gap-1.5">
          <button
            v-for="bt in businessTypes"
            :key="bt.code"
            type="button"
            class="rounded-full border px-3 py-1.5 text-sm font-medium transition-colors"
            :class="bt.code === selectedBtCode
              ? 'border-indigo-600 bg-indigo-600 text-white'
              : 'border-slate-300 bg-white text-slate-600 hover:border-indigo-400 hover:text-indigo-600'"
            @click="selectedBtCode = bt.code"
          >
            {{ bt.code }} {{ bt.displayName ?? '' }}
          </button>
        </div>

        <template v-if="selectedBusinessType">
          <!-- 部署（商品部） -->
          <section class="space-y-2">
            <div class="flex flex-wrap items-center justify-between gap-2">
              <h2 class="flex items-center gap-1.5 text-sm font-semibold text-slate-700">
                <Building2 class="h-4 w-4 text-indigo-500" />
                {{ selectedBusinessType.displayName ?? selectedBusinessType.code }} の部署（商品部）
              </h2>
              <button
                v-if="isOwner"
                type="button"
                class="inline-flex items-center gap-1 rounded-lg border border-slate-300 px-2.5 py-1.5 text-xs font-medium text-slate-600 hover:bg-slate-50"
                @click="openSectionEditor(null)"
              >
                <Plus class="h-3.5 w-3.5" />
                部署を追加
              </button>
            </div>
            <p
              v-if="sections.length === 0"
              class="rounded-xl border border-dashed border-slate-200 bg-white p-6 text-center text-sm text-slate-400"
            >
              部署（商品部）が登録されていません。
            </p>
            <DataTable
              v-else
              :columns="sectionColumns"
              :rows="sections"
              :row-key="(row: OrgSection) => row.id"
            >
              <template #actions="{ row }">
                <div class="flex justify-end gap-1">
                  <button
                    type="button"
                    class="inline-flex items-center gap-1 rounded-md px-1.5 py-1 text-xs text-indigo-600 hover:bg-indigo-50"
                    @click="openSectionEditor(row)"
                  >
                    <Pencil class="h-3.5 w-3.5" />
                    編集
                  </button>
                  <button
                    type="button"
                    class="inline-flex items-center gap-1 rounded-md px-1.5 py-1 text-xs text-rose-600 hover:bg-rose-50"
                    @click="deleteSection(row)"
                  >
                    <Trash2 class="h-3.5 w-3.5" />
                    削除
                  </button>
                </div>
              </template>
            </DataTable>
          </section>

          <!-- 部門 -->
          <section class="space-y-2">
            <div class="flex flex-wrap items-center justify-between gap-2">
              <h2 class="flex items-center gap-1.5 text-sm font-semibold text-slate-700">
                <Building2 class="h-4 w-4 text-indigo-500" />
                {{ selectedBusinessType.displayName ?? selectedBusinessType.code }} の部門
              </h2>
              <button
                v-if="isOwner"
                type="button"
                class="inline-flex items-center gap-1 rounded-lg border border-slate-300 px-2.5 py-1.5 text-xs font-medium text-slate-600 hover:bg-slate-50"
                @click="openDepartmentEditor(null)"
              >
                <Plus class="h-3.5 w-3.5" />
                部門を追加
              </button>
            </div>
            <p
              v-if="departments.length === 0"
              class="rounded-xl border border-dashed border-slate-200 bg-white p-6 text-center text-sm text-slate-400"
            >
              部門が登録されていません。
            </p>
            <DataTable
              v-else
              :columns="departmentColumns"
              :rows="departments"
              :row-key="(row: OrgDepartment) => row.id"
            >
              <template #actions="{ row }">
                <div class="flex justify-end gap-1">
                  <button
                    type="button"
                    class="inline-flex items-center gap-1 rounded-md px-1.5 py-1 text-xs text-indigo-600 hover:bg-indigo-50"
                    @click="openDepartmentEditor(row)"
                  >
                    <Pencil class="h-3.5 w-3.5" />
                    編集
                  </button>
                  <button
                    type="button"
                    class="inline-flex items-center gap-1 rounded-md px-1.5 py-1 text-xs text-rose-600 hover:bg-rose-50"
                    @click="deleteDepartment(row)"
                  >
                    <Trash2 class="h-3.5 w-3.5" />
                    削除
                  </button>
                </div>
              </template>
            </DataTable>
          </section>
        </template>

        <!-- 相談受付デスク -->
        <section class="space-y-2">
          <div class="flex flex-wrap items-center justify-between gap-2">
            <h2 class="flex items-center gap-1.5 text-sm font-semibold text-slate-700">
              <Headset class="h-4 w-4 text-indigo-500" />
              相談受付デスク
            </h2>
            <button
              v-if="isOwner"
              type="button"
              class="inline-flex items-center gap-1 rounded-lg border border-slate-300 px-2.5 py-1.5 text-xs font-medium text-slate-600 hover:bg-slate-50"
              @click="openDeskEditor(null)"
            >
              <Plus class="h-3.5 w-3.5" />
              デスクを追加
            </button>
          </div>
          <p class="text-xs text-slate-400">
            チャット窓口（システム部・商品管理部・物流部）が設定されたデスクは、業務チャットの相談先として表示されます。
          </p>
          <p
            v-if="sortedDesks.length === 0"
            class="rounded-xl border border-dashed border-slate-200 bg-white p-6 text-center text-sm text-slate-400"
          >
            相談受付デスクが登録されていません。
          </p>
          <DataTable
            v-else
            :columns="deskColumns"
            :rows="sortedDesks"
            :row-key="(row: ContactDesk) => row.id"
          >
            <template #actions="{ row }">
              <div class="flex justify-end gap-1">
                <button
                  type="button"
                  class="inline-flex items-center gap-1 rounded-md px-1.5 py-1 text-xs text-indigo-600 hover:bg-indigo-50"
                  @click="openDeskEditor(row)"
                >
                  <Pencil class="h-3.5 w-3.5" />
                  編集
                </button>
                <button
                  type="button"
                  class="inline-flex items-center gap-1 rounded-md px-1.5 py-1 text-xs text-rose-600 hover:bg-rose-50"
                  @click="deleteDesk(row)"
                >
                  <Trash2 class="h-3.5 w-3.5" />
                  削除
                </button>
              </div>
            </template>
          </DataTable>
        </section>
      </div>
    </StatusBlock>

    <!-- 編集ダイアログ（モバイルはボトムシート風） -->
    <div
      v-if="editorOpen"
      class="fixed inset-0 z-40 flex items-end justify-center bg-slate-900/40 sm:items-center sm:p-4"
      @click.self="closeEditor"
    >
      <div class="max-h-[92vh] w-full overflow-y-auto rounded-t-xl bg-white p-4 shadow-xl sm:max-w-lg sm:rounded-xl sm:p-5">
        <div class="mb-3 flex items-center justify-between gap-2">
          <h3 class="text-base font-bold text-slate-800">
            {{ editorIsCreate ? editorTitles[editorKind].create : editorTitles[editorKind].edit }}
          </h3>
          <button
            type="button"
            class="rounded-md p-1 text-slate-400 hover:bg-slate-100 hover:text-slate-600"
            aria-label="閉じる"
            @click="closeEditor"
          >
            <X class="h-5 w-5" />
          </button>
        </div>

        <form class="space-y-3" @submit.prevent="submitEditor">
          <!-- 部署（商品部）フォーム -->
          <template v-if="editorKind === 'section'">
            <div>
              <label class="mb-1 block text-xs font-medium text-slate-500">部署名（必須）</label>
              <input v-model="sectionForm.name" type="text" placeholder="例: 商品1部" class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm">
            </div>
            <div class="grid grid-cols-1 gap-3 sm:grid-cols-2">
              <div>
                <label class="mb-1 block text-xs font-medium text-slate-500">取扱分類</label>
                <input v-model="sectionForm.categoryNote" type="text" placeholder="例: 婦人" class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm">
              </div>
              <div>
                <label class="mb-1 block text-xs font-medium text-slate-500">電話</label>
                <input v-model="sectionForm.phone" type="tel" placeholder="例: 048-631-2151" class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm">
              </div>
              <div>
                <label class="mb-1 block text-xs font-medium text-slate-500">フロア</label>
                <input v-model="sectionForm.floor" type="text" placeholder="例: 2F" class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm">
              </div>
              <div>
                <label class="mb-1 block text-xs font-medium text-slate-500">表示順</label>
                <input v-model.number="sectionForm.displayOrder" type="number" min="1" inputmode="numeric" class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm">
              </div>
            </div>
          </template>

          <!-- 部門フォーム -->
          <template v-else-if="editorKind === 'department'">
            <div class="grid grid-cols-1 gap-3 sm:grid-cols-2">
              <div>
                <label class="mb-1 block text-xs font-medium text-slate-500">部門CD（必須）</label>
                <input v-model="deptForm.deptCode" type="text" placeholder="例: 5A" class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm">
              </div>
              <div>
                <label class="mb-1 block text-xs font-medium text-slate-500">部門名（必須）</label>
                <input v-model="deptForm.deptName" type="text" placeholder="例: カットソー" class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm">
              </div>
              <div>
                <label class="mb-1 block text-xs font-medium text-slate-500">担当商品部</label>
                <select v-model="deptForm.buyerSectionId" class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm">
                  <option :value="null">未設定</option>
                  <option v-for="section in sections" :key="section.id" :value="section.id">
                    {{ section.name }}<template v-if="section.categoryNote">（{{ section.categoryNote }}）</template>
                  </option>
                </select>
              </div>
              <div>
                <label class="mb-1 block text-xs font-medium text-slate-500">表示順</label>
                <input v-model.number="deptForm.displayOrder" type="number" min="1" inputmode="numeric" class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm">
              </div>
            </div>
          </template>

          <!-- 相談受付デスクフォーム -->
          <template v-else>
            <div>
              <label class="mb-1 block text-xs font-medium text-slate-500">相談内容（必須）</label>
              <input v-model="deskForm.topic" type="text" placeholder="例: お支払い関連" class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm">
            </div>
            <div class="grid grid-cols-1 gap-3 sm:grid-cols-2">
              <div>
                <label class="mb-1 block text-xs font-medium text-slate-500">担当部署（必須）</label>
                <input v-model="deskForm.departmentName" type="text" placeholder="例: 経理部" class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm">
              </div>
              <div>
                <label class="mb-1 block text-xs font-medium text-slate-500">電話</label>
                <input v-model="deskForm.phone" type="tel" placeholder="例: 048-631-2134" class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm">
              </div>
              <div>
                <label class="mb-1 block text-xs font-medium text-slate-500">チャット窓口</label>
                <select v-model="deskForm.chatDomain" class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm">
                  <option value="">なし（電話のみ）</option>
                  <option v-for="domain in CHAT_DOMAINS" :key="domain" :value="domain">
                    {{ CHAT_DOMAIN_LABELS[domain] }}
                  </option>
                </select>
              </div>
              <div>
                <label class="mb-1 block text-xs font-medium text-slate-500">表示順</label>
                <input v-model.number="deskForm.displayOrder" type="number" min="1" inputmode="numeric" class="w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm">
              </div>
            </div>
          </template>

          <div v-if="editorError" class="rounded-lg bg-red-50 p-3 text-sm text-red-700">
            <p class="font-medium">[{{ editorError.errorCode }}] {{ editorError.summary }}</p>
            <p v-if="editorError.remedy" class="text-xs text-red-500">{{ editorError.remedy }}</p>
          </div>

          <div class="flex items-center justify-end gap-2 pt-1">
            <button
              type="button"
              class="rounded-lg border border-slate-300 px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50"
              :disabled="editorSaving"
              @click="closeEditor"
            >
              キャンセル
            </button>
            <button
              type="submit"
              class="inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700 disabled:opacity-50"
              :disabled="editorSaving"
            >
              <LoaderCircle v-if="editorSaving" class="h-4 w-4 animate-spin" />
              保存
            </button>
          </div>
        </form>
      </div>
    </div>
  </div>
</template>
