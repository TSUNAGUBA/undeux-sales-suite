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
  BarChart3,
} from 'lucide-vue-next'

const emit = defineEmits<{ navigate: [] }>()

const route = useRoute()
const router = useRouter()
const { user, logout } = useAuth()

const navItems = [
  { to: '/', label: '全社サマリー', icon: LayoutDashboard },
  { to: '/sales', label: '売上分析', icon: TrendingUp },
  { to: '/products', label: '商品別分析', icon: Package },
  { to: '/inventory', label: '在庫・発注分析', icon: Boxes },
  { to: '/crosstab', label: 'クロス集計', icon: LayoutGrid },
  { to: '/product-master', label: '商品マスタ', icon: Shirt },
  { to: '/product-analytics', label: '商品軸分析', icon: BarChart3 },
  { to: '/imports', label: '週次取込', icon: Upload },
]

/**
 * サイドバー項目のアクティブ判定。サブルートを持つメニュー（商品マスタ・商品軸分析）は
 * 親パス + '/' 以下のサブルートでもアクティブ表示する。完全一致のみのページは === で判定。
 */
function isActive(path: string): boolean {
  if (path === '/product-analytics' || path === '/product-master') {
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
  <aside class="flex h-full w-64 flex-col bg-slate-900 text-slate-200">
    <div class="border-b border-slate-700/60 px-5 py-5">
      <p class="text-lg font-bold text-white">UndeuxSales</p>
      <p class="text-xs text-slate-400">売上参照スイート</p>
    </div>

    <nav class="flex-1 space-y-1 overflow-y-auto px-3 py-4">
      <NuxtLink
        v-for="item in navItems"
        :key="item.to"
        :to="item.to"
        class="flex items-center gap-3 rounded-lg px-3 py-2.5 text-sm font-medium transition-colors"
        :class="
          isActive(item.to)
            ? 'bg-indigo-600 text-white'
            : 'text-slate-300 hover:bg-slate-800 hover:text-white'
        "
        @click="emit('navigate')"
      >
        <component :is="item.icon" class="h-5 w-5 shrink-0" />
        <span>{{ item.label }}</span>
      </NuxtLink>
    </nav>

    <div class="border-t border-slate-700/60 p-3">
      <p class="truncate px-2 text-xs text-slate-400" :title="user?.email ?? ''">
        {{ user?.email ?? 'ゲスト' }}
      </p>
      <button
        type="button"
        class="mt-2 flex w-full items-center gap-2 rounded-lg px-3 py-2 text-sm font-medium text-slate-300 transition-colors hover:bg-slate-800 hover:text-white"
        @click="handleLogout"
      >
        <LogOut class="h-4 w-4" />
        <span>ログアウト</span>
      </button>
    </div>
  </aside>
</template>
