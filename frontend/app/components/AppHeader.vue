<script setup lang="ts">
import { ArrowLeft, ChevronRight, LogOut } from 'lucide-vue-next'
import type { AccountType } from '~/types/api'

/**
 * 全ページ共通ヘッダー。ブランド（ホームへのリンク）＋現在カテゴリのパンくず＋
 * 戻るボタン（1つ上の階層へ）＋アカウント種別切替（デモ用）＋ユーザー情報・ログアウトを担う。
 * パンくず・戻る先は utils/navigation.ts（メニュー構成の SoT）から導出する。
 */
const route = useRoute()
const router = useRouter()
const { user, logout } = useAuth()
const { accountType, setAccountType } = useAccountType()

const currentCategory = computed(() => visibleCategoryForPath(route.path, accountType.value))
const parentPath = computed(() => findParentPath(route.path))

// デモ用アカウント種別切替。実運用ではクレームが SoT のため非表示にする想定。
const ROLE_OPTIONS: ReadonlyArray<{ value: AccountType; label: string }> = [
  { value: 'supplier', label: 'メーカー' },
  { value: 'buyer', label: '小売' },
]

/**
 * アカウント種別を切り替える。切替後の現在パスが新ロールで不可なら、ロールに合わせて
 * ホーム（目的別メニュー）へ戻す（許可済みページに留まれる場合はその場に留まる）。
 */
function switchRole(value: AccountType): void {
  if (value === accountType.value) return
  setAccountType(value)
  if (!isPathAllowedForRole(route.path, value)) {
    void router.push('/')
  }
}

const loggingOut = ref(false)
const logoutFailed = ref(false)

async function handleLogout(): Promise<void> {
  if (loggingOut.value) return
  loggingOut.value = true
  logoutFailed.value = false
  try {
    await logout()
    await router.push('/login')
  } catch (error) {
    // signOut 失敗時はセッションが残ったままのため、ログイン画面へは遷移せず
    // 失敗を明示して再試行を促す（「ログアウトしたつもり」を防ぐ）。
    console.error('[AppHeader] ログアウトに失敗しました:', error)
    logoutFailed.value = true
  } finally {
    loggingOut.value = false
  }
}
</script>

<template>
  <header
    class="flex h-14 shrink-0 items-center gap-1.5 border-b border-slate-200 bg-white px-3 lg:px-6"
  >
    <NuxtLink
      v-if="parentPath"
      :to="parentPath"
      class="flex h-11 w-11 shrink-0 items-center justify-center rounded-lg text-slate-500 transition-colors hover:bg-slate-100 hover:text-slate-800"
      aria-label="1つ上の階層へ戻る"
      title="1つ上の階層へ戻る"
    >
      <ArrowLeft class="h-4 w-4" />
    </NuxtLink>

    <nav class="flex min-w-0 items-center" aria-label="パンくず">
      <ol class="flex min-w-0 items-center gap-1">
        <li class="flex shrink-0 items-center">
          <NuxtLink
            to="/"
            class="rounded-lg px-1.5 py-3 text-sm font-bold text-slate-800 transition-colors hover:text-indigo-700"
            title="ホーム（目的別メニュー）へ"
          >
            UndeuxSales
          </NuxtLink>
        </li>
        <li v-if="currentCategory" class="flex min-w-0 items-center gap-1">
          <ChevronRight class="h-3.5 w-3.5 shrink-0 text-slate-400" aria-hidden="true" />
          <NuxtLink
            :to="categoryDefaultPath(currentCategory)"
            class="flex min-w-0 items-center gap-1.5 rounded-lg px-1.5 py-3 text-sm font-medium text-slate-600 transition-colors hover:text-indigo-700"
            :title="`${currentCategory.label}の先頭ページへ`"
          >
            <component :is="currentCategory.icon" class="h-4 w-4 shrink-0" />
            <span class="truncate">{{ currentCategory.label }}</span>
          </NuxtLink>
        </li>
      </ol>
    </nav>

    <!--
      右ブロックは shrink-0 にしない（モバイルでエラー文言が出てもヘッダーを
      横溢れさせず、文言側を truncate で縮めて再試行ボタンの操作性を常に保つ）。
    -->
    <div class="ml-auto flex min-w-0 items-center gap-2">
      <!-- アカウント種別切替（デモ用）。メーカー=サプライヤー／小売=バイヤー。
           実運用ではカスタムクレームが SoT のため非表示にする想定。 -->
      <div
        class="flex shrink-0 items-center rounded-lg border border-slate-200 p-0.5"
        role="group"
        aria-label="アカウント種別の切替（デモ）"
      >
        <button
          v-for="opt in ROLE_OPTIONS"
          :key="opt.value"
          type="button"
          class="rounded-md px-2 py-1 text-xs font-medium transition-colors"
          :class="
            accountType === opt.value
              ? 'bg-indigo-600 text-white'
              : 'text-slate-500 hover:text-slate-800'
          "
          :aria-pressed="accountType === opt.value"
          :title="`${opt.label}として表示`"
          @click="switchRole(opt.value)"
        >
          {{ opt.label }}
        </button>
      </div>

      <span
        v-if="logoutFailed"
        class="min-w-0 truncate text-xs text-rose-600"
        role="alert"
        title="ログアウトに失敗しました。通信環境を確認して再試行してください。"
      >
        ログアウトに失敗しました。再試行してください。
      </span>
      <span
        v-else
        class="hidden max-w-44 truncate text-xs text-slate-400 sm:block"
        :title="user?.email ?? ''"
      >
        {{ user?.email ?? 'ゲスト' }}
      </span>
      <button
        type="button"
        class="flex h-11 shrink-0 items-center gap-1.5 rounded-lg px-2.5 text-sm font-medium text-slate-500 transition-colors hover:bg-slate-100 hover:text-slate-800 disabled:cursor-not-allowed disabled:opacity-50"
        :disabled="loggingOut"
        aria-label="ログアウト"
        title="ログアウト"
        @click="handleLogout"
      >
        <LogOut class="h-4 w-4 shrink-0" />
        <span class="hidden sm:inline">{{ loggingOut ? 'ログアウト中…' : 'ログアウト' }}</span>
      </button>
    </div>
  </header>
</template>
