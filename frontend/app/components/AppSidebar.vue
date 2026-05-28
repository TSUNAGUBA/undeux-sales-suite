<script setup lang="ts">
import {
  LayoutDashboard,
  TrendingUp,
  Package,
  Boxes,
  LayoutGrid,
  Upload,
  LogOut,
  Shirt,
  ChevronLeft,
  ChevronRight,
} from 'lucide-vue-next'

const props = withDefaults(
  defineProps<{
    /** 折りたたみ（アイコンのみ）表示するか。PC（lg+）でのみ意味を持つ。 */
    collapsed?: boolean
    /** 折りたたみトグルボタンを表示するか。モバイルドロワーでは非表示にする。 */
    showCollapseToggle?: boolean
  }>(),
  {
    collapsed: false,
    showCollapseToggle: false,
  },
)

const emit = defineEmits<{
  navigate: []
  'toggle-collapsed': []
}>()

const route = useRoute()
const router = useRouter()
const { user, logout } = useAuth()

// 商品軸分析（/product-analytics）はサイドバーから隠す。ルート自体は残っており、
// 必要に応じて直接 URL でアクセスできる。
const navItems = [
  { to: '/', label: '全社サマリー', icon: LayoutDashboard },
  { to: '/sales', label: '売上分析', icon: TrendingUp },
  { to: '/products', label: '商品別分析', icon: Package },
  { to: '/inventory', label: '在庫・発注分析', icon: Boxes },
  { to: '/crosstab', label: 'クロス集計', icon: LayoutGrid },
  { to: '/product-master', label: '商品マスタ', icon: Shirt },
  { to: '/imports', label: '週次取込', icon: Upload },
]

/**
 * サイドバー項目のアクティブ判定。サブルートを持つメニュー（商品マスタ）は
 * 親パス + '/' 以下のサブルートでもアクティブ表示する。完全一致のみのページは === で判定。
 */
function isActive(path: string): boolean {
  if (path === '/product-master') {
    return route.path === path || route.path.startsWith(`${path}/`)
  }
  return route.path === path
}

async function handleLogout(): Promise<void> {
  await logout()
  await router.push('/login')
}
</script>

<template>
  <aside
    class="flex h-full flex-col bg-slate-900 text-slate-200 transition-[width] duration-200 ease-out"
    :class="props.collapsed ? 'w-16' : 'w-64'"
    :aria-label="props.collapsed ? 'メインナビゲーション（折りたたみ中）' : 'メインナビゲーション'"
  >
    <div
      class="flex items-center border-b border-slate-700/60"
      :class="props.collapsed ? 'justify-center px-2 py-5' : 'justify-between gap-2 px-5 py-5'"
    >
      <div v-if="!props.collapsed" class="min-w-0">
        <p class="text-lg font-bold text-white">UndeuxSales</p>
        <p class="text-xs text-slate-400">売上参照スイート</p>
      </div>
      <button
        v-if="props.showCollapseToggle"
        type="button"
        class="flex h-8 w-8 shrink-0 items-center justify-center rounded-lg text-slate-300 transition-colors hover:bg-slate-800 hover:text-white"
        :aria-label="props.collapsed ? 'サイドメニューを開く' : 'サイドメニューを閉じる'"
        :aria-expanded="!props.collapsed"
        :title="props.collapsed ? 'サイドメニューを開く' : 'サイドメニューを閉じる'"
        @click="emit('toggle-collapsed')"
      >
        <ChevronLeft v-if="!props.collapsed" class="h-4 w-4" />
        <ChevronRight v-else class="h-4 w-4" />
      </button>
    </div>

    <nav
      class="flex-1 space-y-1 overflow-y-auto py-4"
      :class="props.collapsed ? 'px-2' : 'px-3'"
    >
      <NuxtLink
        v-for="item in navItems"
        :key="item.to"
        :to="item.to"
        class="flex items-center rounded-lg py-2.5 text-sm font-medium transition-colors"
        :class="[
          props.collapsed ? 'justify-center px-2' : 'gap-3 px-3',
          isActive(item.to)
            ? 'bg-indigo-600 text-white'
            : 'text-slate-300 hover:bg-slate-800 hover:text-white',
        ]"
        :title="props.collapsed ? item.label : undefined"
        :aria-label="props.collapsed ? item.label : undefined"
        @click="emit('navigate')"
      >
        <component :is="item.icon" class="h-5 w-5 shrink-0" />
        <span v-if="!props.collapsed">{{ item.label }}</span>
      </NuxtLink>
    </nav>

    <div
      class="border-t border-slate-700/60"
      :class="props.collapsed ? 'p-2' : 'p-3'"
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
        :class="props.collapsed ? 'justify-center px-2' : 'mt-2 gap-2 px-3'"
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
