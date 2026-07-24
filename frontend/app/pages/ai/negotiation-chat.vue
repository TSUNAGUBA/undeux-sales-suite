<script setup lang="ts">
/**
 * 商談チャット（/ai/negotiation-chat）。
 *
 * 上部に業態の chips を並べ、選択された業態配下の部門をその下に chips として並べて選択する
 * （要件どおりの2段 chips 構成）。部門は担当の商品部（バイヤー部署）ごとにグルーピングして表示し、
 * 業態＋部門を選ぶと、その部門のバイヤーを想定した AI と商談のシミュレーションができる。
 */
import { Building2, Handshake, MapPin, Phone, TriangleAlert } from 'lucide-vue-next'
import type {
  OrgBusinessType,
  OrgDepartment,
  OrgMasterResponse,
  OrgSection,
  RagStatus,
} from '~/types/api'

useHead({ title: '商談チャット | UndeuxSales' })

const { get } = useApi()
const { messages, streaming, send, stop, clear } = useChatConversation()

// ---------------------------------------------------------------
// 初期ロード（RAG ステータス + 組織マスタ）
// ---------------------------------------------------------------
const ragStatus = ref<RagStatus | null>(null)
const businessTypes = ref<OrgBusinessType[]>([])
const loading = ref(true)
const errorMessage = ref<string | null>(null)

async function load(): Promise<void> {
  loading.value = true
  errorMessage.value = null
  try {
    const [statusRes, orgRes] = await Promise.all([
      get<RagStatus>('/api/rag/status'),
      get<OrgMasterResponse>('/api/org-master'),
    ])
    ragStatus.value = statusRes
    businessTypes.value = orgRes.businessTypes
  } catch (error) {
    errorMessage.value = apiErrorMessage(error)
  } finally {
    loading.value = false
  }
}

onMounted(load)

const aiConfigured = computed(() => ragStatus.value?.aiConfigured ?? false)

// ---------------------------------------------------------------
// 業態・部門の選択（業態 chips → 配下部門 chips の2段構成）
// ---------------------------------------------------------------
const selectedBtCode = ref<string | null>(null)
const selectedDeptId = ref<number | null>(null)

const selectedBusinessType = computed(
  () => businessTypes.value.find((bt) => bt.code === selectedBtCode.value) ?? null,
)
const selectedDepartment = computed(
  () =>
    selectedBusinessType.value?.departments.find((dept) => dept.id === selectedDeptId.value) ??
    null,
)

/** 部門 chips のグループ（担当の商品部ごと。部署未割当は「部署未設定」にまとめる）。 */
interface DeptGroup {
  key: string
  label: string
  departments: OrgDepartment[]
}

const deptGroups = computed<DeptGroup[]>(() => {
  const bt = selectedBusinessType.value
  if (!bt) return []
  const sorted = [...bt.departments].sort((a, b) => a.displayOrder - b.displayOrder)
  const bySection = new Map<number, OrgDepartment[]>()
  const unassigned: OrgDepartment[] = []
  const sectionIds = new Set(bt.sections.map((section) => section.id))
  for (const dept of sorted) {
    if (dept.buyerSectionId !== null && sectionIds.has(dept.buyerSectionId)) {
      const list = bySection.get(dept.buyerSectionId) ?? []
      list.push(dept)
      bySection.set(dept.buyerSectionId, list)
    } else {
      unassigned.push(dept)
    }
  }
  const groups: DeptGroup[] = []
  for (const section of [...bt.sections].sort((a, b) => a.displayOrder - b.displayOrder)) {
    const departments = bySection.get(section.id)
    if (!departments || departments.length === 0) continue
    groups.push({ key: `section-${section.id}`, label: sectionLabel(section), departments })
  }
  if (unassigned.length > 0) {
    groups.push({ key: 'unassigned', label: '部署未設定', departments: unassigned })
  }
  return groups
})

function sectionLabel(section: OrgSection): string {
  return section.categoryNote ? `${section.name}（${section.categoryNote}）` : section.name
}

/** 会話がある場合は確認のうえクリアする。false ならユーザーがキャンセルした。 */
function guardReset(): boolean {
  if (messages.value.length === 0) return true
  if (!window.confirm('選択を変更すると会話がクリアされます。よろしいですか？')) return false
  clear()
  return true
}

function selectBusinessType(bt: OrgBusinessType): void {
  if (bt.code === selectedBtCode.value || bt.departments.length === 0) return
  if (!guardReset()) return
  selectedBtCode.value = bt.code
  selectedDeptId.value = null
}

function selectDepartment(dept: OrgDepartment): void {
  if (dept.id === selectedDeptId.value) return
  if (!guardReset()) return
  selectedDeptId.value = dept.id
}

/** 商談相手のペルソナ表示（業態／商品部／部門 + 連絡先）。 */
const persona = computed(() => {
  const bt = selectedBusinessType.value
  const dept = selectedDepartment.value
  if (!bt || !dept) return null
  const section =
    dept.buyerSectionId !== null
      ? bt.sections.find((s) => s.id === dept.buyerSectionId) ?? null
      : null
  const parts = [bt.displayName ?? bt.code]
  if (section) parts.push(sectionLabel(section))
  parts.push(`${dept.deptCode} ${dept.deptName}`)
  return {
    title: `${parts.join('／')} のバイヤーと商談中`,
    phone: section?.phone ?? null,
    floor: section?.floor ?? null,
  }
})

// ---------------------------------------------------------------
// 送信
// ---------------------------------------------------------------
function onSend(text: string): void {
  const bt = selectedBusinessType.value
  const dept = selectedDepartment.value
  if (!bt || !dept || !aiConfigured.value) return
  send(text, '/api/chat/negotiation', {
    businessTypeCode: bt.code,
    deptCode: dept.deptCode,
  })
}
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-3">
    <div class="shrink-0">
      <h1 class="text-xl font-bold text-slate-800">商談チャット</h1>
      <p class="text-sm text-slate-500">
        業態と部門を選ぶと、その部門のバイヤーを想定した AI と商談のシミュレーションができます。
      </p>
    </div>

    <StatusBlock :loading="loading" :error="errorMessage" :empty="businessTypes.length === 0" empty-message="業態マスタが登録されていません。">
      <div class="shrink-0 space-y-3">
        <!-- AI 未設定の案内 -->
        <div
          v-if="!aiConfigured"
          class="flex items-start gap-2 rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-700"
        >
          <TriangleAlert class="mt-0.5 h-4 w-4 shrink-0" />
          <p><span class="font-medium">AI 未設定</span>：AI が未設定のため、チャットを利用できません。運営者にお問い合わせください。</p>
        </div>

        <!-- 業態 chips（上段） -->
        <div class="rounded-xl border border-slate-200 bg-white p-3 shadow-sm sm:p-4">
          <p class="mb-1.5 flex items-center gap-1 text-xs font-medium text-slate-500">
            <Building2 class="h-3.5 w-3.5" />
            業態を選択
          </p>
          <div class="flex flex-wrap gap-1.5">
            <button
              v-for="bt in businessTypes"
              :key="bt.code"
              type="button"
              :disabled="bt.departments.length === 0"
              :title="bt.departments.length === 0 ? '部門未登録' : undefined"
              class="rounded-full border px-3 py-1.5 text-sm font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-40"
              :class="bt.code === selectedBtCode
                ? 'border-indigo-600 bg-indigo-600 text-white'
                : 'border-slate-300 bg-white text-slate-600 hover:border-indigo-400 hover:text-indigo-600'"
              @click="selectBusinessType(bt)"
            >
              {{ bt.displayName ?? bt.code }}
            </button>
          </div>

          <!-- 部門 chips（下段・選択業態の配下。商品部ごとにグルーピング） -->
          <div v-if="selectedBusinessType" class="mt-3 space-y-2 border-t border-slate-100 pt-3">
            <p class="text-xs font-medium text-slate-500">
              {{ selectedBusinessType.displayName ?? selectedBusinessType.code }} の部門を選択
            </p>
            <div v-for="group in deptGroups" :key="group.key">
              <p class="mb-1 text-[11px] font-medium text-slate-400">{{ group.label }}</p>
              <div class="flex flex-wrap gap-1.5">
                <button
                  v-for="dept in group.departments"
                  :key="dept.id"
                  type="button"
                  class="rounded-full border px-2.5 py-1 text-xs font-medium transition-colors"
                  :class="dept.id === selectedDeptId
                    ? 'border-indigo-600 bg-indigo-600 text-white'
                    : 'border-slate-300 bg-white text-slate-600 hover:border-indigo-400 hover:text-indigo-600'"
                  @click="selectDepartment(dept)"
                >
                  {{ dept.deptCode }} {{ dept.deptName }}
                </button>
              </div>
            </div>
            <p v-if="deptGroups.length === 0" class="text-xs text-slate-400">
              この業態には部門が登録されていません。
            </p>
          </div>
        </div>

        <!-- ペルソナヘッダー -->
        <div
          v-if="persona"
          class="flex flex-wrap items-center gap-x-4 gap-y-1 rounded-xl border border-indigo-200 bg-indigo-50/60 px-4 py-2.5"
        >
          <p class="flex items-center gap-1.5 text-sm font-medium text-indigo-800">
            <Handshake class="h-4 w-4 shrink-0 text-indigo-500" />
            {{ persona.title }}
          </p>
          <p v-if="persona.phone" class="flex items-center gap-1 text-xs text-indigo-600/80">
            <Phone class="h-3 w-3" />
            {{ persona.phone }}
          </p>
          <p v-if="persona.floor" class="flex items-center gap-1 text-xs text-indigo-600/80">
            <MapPin class="h-3 w-3" />
            {{ persona.floor }}
          </p>
        </div>
      </div>

      <!-- チャット -->
      <div v-if="persona" class="flex min-h-[20rem] flex-1 flex-col">
        <ChatPanel
          class="flex-1"
          :messages="messages"
          :streaming="streaming"
          :disabled="!aiConfigured"
          :placeholder="aiConfigured ? 'バイヤーへの提案・質問を入力...' : 'AI 未設定のため送信できません'"
          empty-hint="商談したい内容（新商品の提案、価格交渉、納期相談など）を入力してください。選択した部門のバイヤーを想定した応答を返します。"
          @send="onSend"
          @stop="stop"
          @clear="clear"
        />
      </div>
      <div
        v-else
        class="flex flex-1 items-center justify-center rounded-xl border border-dashed border-slate-200 bg-white/60 p-8"
      >
        <p class="text-sm text-slate-400">業態と部門を選択すると、商談チャットを開始できます。</p>
      </div>
    </StatusBlock>
  </div>
</template>
