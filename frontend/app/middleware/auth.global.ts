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

  // アカウント種別（ロール）で許可されないページはホーム（目的別メニュー）へ誘導する。
  // 認証状態の確定（ensureReady）後はアカウント種別も解決済み（firebase.client プラグインが
  // authReady を立てる前にクレーム/デモ上書きを反映する）。
  const { accountType } = useAccountType()
  if (!isPathAllowedForRole(to.path, accountType.value)) {
    return navigateTo('/')
  }

  return undefined
})
