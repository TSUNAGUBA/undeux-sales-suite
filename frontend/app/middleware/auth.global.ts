// 全ルートに適用する認証ガード。未認証ユーザーをログイン画面へ誘導する。
export default defineNuxtRouteMiddleware(async (to) => {
  const { authReady, isAuthenticated, ensureReady } = useAuth()

  // 初回アクセス時は Firebase の認証状態復元を待つ。
  if (!authReady.value) {
    await ensureReady()
  }

  if (to.path === '/login') {
    return isAuthenticated.value ? navigateTo('/') : undefined
  }

  if (!isAuthenticated.value) {
    return navigateTo('/login')
  }

  return undefined
})
