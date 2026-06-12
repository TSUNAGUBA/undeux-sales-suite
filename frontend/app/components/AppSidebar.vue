<script setup lang="ts">
import {
  LayoutDashboard,
  TrendingUp,
  Package,
  Boxes,
  LayoutGrid,
  ListOrdered,
  ScatterChart,
  SlidersHorizontal,
  Upload,
  LogOut,
  Shirt,
  CalendarPlus,
  ChevronLeft,
  ChevronRight,
  X,
} from 'lucide-vue-next'
import { SIDEBAR_WIDTH_EXPANDED, SIDEBAR_WIDTH_COLLAPSED } from '~/composables/useSidebar'

const props = withDefaults(
  defineProps<{
    /** 折りたたみ（アイコンのみ）表示するか。PC（lg+）でのみ意味を持つ。 */
    collapsed?: boolean
    /**
     * PC 用の折りたたみトグルボタンを表示するか。
     * `showCloseButton` と排他。両方 true を指定した場合は本ボタンが優先される。
     */
    showCollapseToggle?: boolean
    /**
     * モバイル用のドロワー閉じるボタンを表示するか。
     * `showCollapseToggle` と排他。両方 true を指定した場合は表示されない。
     */
    showCloseButton?: boolean
    /** aria-controls の参照先 ID。トグルボタンとの関連付けに使う。 */
    sidebarId?: string
    /**
     * 初回マウント完了までトランジションを抑止するか。FOUC 防止のため、
     * デフォルトは false。利用者は親レイアウトの onMounted 後に true を渡すこと。
     */
    transitionsEnabled?: boolean
  }>(),
  {
    collapsed: false,
    showCollapseToggle: false,
    showCloseButton: false,
    sidebarId: undefined,
    transitionsEnabled: false,
  },
)

const emit = defineEmits<{
  navigate: []
  'toggle-collapsed': []
  close: []
}>()

const route = useRoute()
const router = useRouter()
const { user, logout } = useAuth()

// 分析ページはスタースキーマ（/mart 配下）が正。プロトタイプ段階の旧ページは廃止済み。
const navGroups = [
  {
    header: null as string | null,
    items: [
      { to: '/mart', label: '全社サマリー', icon: LayoutDashboard },
      { to: '/mart/sales', label: '売上分析', icon: TrendingUp },
      { to: '/mart/products', label: '商品別分析', icon: Package },
      { to: '/mart/inventory', label: '在庫・発注分析', icon: Boxes },
      { to: '/mart/crosstab', label: 'クロス集計', icon: LayoutGrid },
      { to: '/mart/ranking', label: 'ランキング分析', icon: ListOrdered },
      { to: '/mart/scatter', label: '散布図・回帰分析', icon: ScatterChart },
      { to: '/mart/simulation', label: '重回帰シミュレーター', icon: SlidersHorizontal },
      { to: '/mart/introductions', label: '商品導入管理', icon: CalendarPlus },
    ],
  },
  {
    header: 'データ管理',
    items: [
      { to: '/product-master', label: '商品マスタ', icon: Shirt },
      { to: '/imports', label: '週次取込', icon: Upload },
    ],
  },
]

/** サブルート（詳細ページ）でも親メニューをアクティブ表示するパス。 */
const SUBROUTE_ACTIVE_PATHS = new Set(['/product-master', '/mart/products'])

/**
 * サイドバー項目のアクティブ判定。詳細サブルートを持つメニュー（商品マスタ・商品別分析）は
 * 親パス + '/' 以下のサブルートでもアクティブ表示する。完全一致のみのページは === で判定。
 * 全社サマリー `/mart` は厳密一致のため、`/mart/sales` 等の配下では非アクティブになる。
 */
function isActive(path: string): boolean {
  if (SUBROUTE_ACTIVE_PATHS.has(path)) {
    return route.path === path || route.path.startsWith(`${path}/`)
  }
  return route.path === path
}

async function handleLogout(): Promise<void> {
  await logout()
  await router.push('/login')
}

// テンプレートを薄くするため class 切替は computed に集約する。
// 幅クラスは useSidebar の定数（SoT）を参照することで、useSidebar 側の
// mainPaddingClass と必ず対応するサイズに揃える。
const asideWidthClass = computed(() =>
  props.collapsed ? SIDEBAR_WIDTH_COLLAPSED : SIDEBAR_WIDTH_EXPANDED,
)
const asideTransitionClass = computed(() =>
  props.transitionsEnabled ? 'transition-[width] duration-200 ease-out' : '',
)
const headerLayoutClass = computed(() =>
  props.collapsed ? 'justify-center px-2 py-5' : 'justify-between gap-2 px-5 py-5',
)
const navPaddingClass = computed(() => (props.collapsed ? 'px-2' : 'px-3'))
const navItemLayoutClass = computed(() =>
  props.collapsed ? 'justify-center px-2' : 'gap-3 px-3',
)
const footerPaddingClass = computed(() => (props.collapsed ? 'p-2' : 'p-3'))
const logoutButtonLayoutClass = computed(() =>
  props.collapsed ? 'justify-center px-2' : 'mt-2 gap-2 px-3',
)
const ariaLabelText = computed(() =>
  props.collapsed ? 'メインナビゲーション（折りたたみ中）' : 'メインナビゲーション',
)
</script>

<template>
  <aside
    :id="props.sidebarId"
    class="flex h-full flex-col bg-slate-900 text-slate-200"
    :class="[asideWidthClass, asideTransitionClass]"
    :aria-label="ariaLabelText"
  >
    <div
      class="flex items-center border-b border-slate-700/60"
      :class="headerLayoutClass"
    >
      <div v-if="!props.collapsed" class="min-w-0">
        <p class="text-lg font-bold text-white">UndeuxSales</p>
        <p class="text-xs text-slate-400">売上参照スイート</p>
      </div>
      <button
        v-if="props.showCollapseToggle"
        type="button"
        class="flex h-11 w-11 shrink-0 items-center justify-center rounded-lg text-slate-300 transition-colors hover:bg-slate-800 hover:text-white"
        :aria-label="props.collapsed ? 'サイドメニューを開く' : 'サイドメニューを閉じる'"
        :aria-expanded="!props.collapsed"
        :aria-controls="props.sidebarId"
        :title="props.collapsed ? 'サイドメニューを開く' : 'サイドメニューを閉じる'"
        @click="emit('toggle-collapsed')"
      >
        <ChevronLeft v-if="!props.collapsed" class="h-4 w-4" />
        <ChevronRight v-else class="h-4 w-4" />
      </button>
      <button
        v-else-if="props.showCloseButton"
        type="button"
        class="flex h-11 w-11 shrink-0 items-center justify-center rounded-lg text-slate-300 transition-colors hover:bg-slate-800 hover:text-white"
        aria-label="サイドメニューを閉じる"
        :aria-controls="props.sidebarId"
        title="サイドメニューを閉じる"
        @click="emit('close')"
      >
        <X class="h-4 w-4" />
      </button>
    </div>

    <nav
      class="flex-1 space-y-1 overflow-y-auto py-4"
      :class="navPaddingClass"
    >
      <div v-for="(group, groupIndex) in navGroups" :key="groupIndex" class="space-y-1">
        <template v-if="group.header">
          <p
            v-if="!props.collapsed"
            class="px-3 pb-1 pt-3 text-[10px] font-semibold uppercase tracking-wider text-slate-500"
          >
            {{ group.header }}
          </p>
          <div
            v-else
            class="mx-2 my-2 border-t border-slate-700/60"
            aria-hidden="true"
          />
        </template>
        <NuxtLink
          v-for="item in group.items"
          :key="item.to"
          :to="item.to"
          class="flex items-center rounded-lg py-2.5 text-sm font-medium transition-colors"
          :class="[
            navItemLayoutClass,
            isActive(item.to)
              ? 'bg-indigo-600 text-white'
              : 'text-slate-300 hover:bg-slate-800 hover:text-white',
          ]"
          :title="props.collapsed ? item.label : undefined"
          :aria-label="props.collapsed ? item.label : undefined"
          @click="emit('navigate')"
        >
          <component :is="item.icon" class="h-5 w-5 shrink-0" />
          <span v-if="!props.collapsed" class="truncate">{{ item.label }}</span>
        </NuxtLink>
      </div>
    </nav>

    <div
      class="border-t border-slate-700/60"
      :class="footerPaddingClass"
    >
      <p
        v-if="!props.collapsed"
        class="truncate px-2 text-xs text-slate-400"
        :title="user?.email ?? ''"
      >
        {{ user?.email ?? 'ゲスト' }}
      </p>
      <button
        type="button"
        class="flex w-full items-center rounded-lg py-2 text-sm font-medium text-slate-300 transition-colors hover:bg-slate-800 hover:text-white"
        :class="logoutButtonLayoutClass"
        :title="props.collapsed ? 'ログアウト' : undefined"
        :aria-label="props.collapsed ? 'ログアウト' : undefined"
        @click="handleLogout"
      >
        <LogOut class="h-4 w-4 shrink-0" />
        <span v-if="!props.collapsed">ログアウト</span>
      </button>
    </div>
  </aside>
</template>
