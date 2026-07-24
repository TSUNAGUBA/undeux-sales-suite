import { initializeApp } from 'firebase/app'
import { getAuth, onAuthStateChanged, type Auth } from 'firebase/auth'
import type { AuthUser } from '~/types/api'

// Firebase Authentication を初期化し、認証状態を Nuxt の状態へ反映する。
export default defineNuxtPlugin(() => {
  const config = useRuntimeConfig()
  const firebase = config.public.firebase as {
    apiKey: string
    authDomain: string
    projectId: string
  }

  const user = useState<AuthUser | null>('auth-user', () => null)
  const authReady = useState<boolean>('auth-ready', () => false)
  const { applyFromClaim, applyOwnerFromClaim } = useAccountType()

  // Firebase 未設定時もアプリが停止しないよう、準備完了として扱う。
  // 未設定でもデモ用のアカウント種別（localStorage 上書き）は反映する。
  if (!firebase.apiKey) {
    applyFromClaim(undefined)
    applyOwnerFromClaim(undefined)
    authReady.value = true
    return { provide: { firebaseAuth: null as Auth | null } }
  }

  const app = initializeApp({
    apiKey: firebase.apiKey,
    authDomain: firebase.authDomain,
    projectId: firebase.projectId,
  })
  const auth = getAuth(app)

  onAuthStateChanged(auth, async (firebaseUser) => {
    user.value = firebaseUser
      ? { uid: firebaseUser.uid, email: firebaseUser.email }
      : null
    // アカウント種別はカスタムクレーム accountType を SoT とする（デモ上書きがあれば優先）。
    // 管理者判定はカスタムクレーム role を SoT とする（role==='admin' のみ管理者。上書きなし）。
    // クレーム取得失敗（ネットワーク等）でも認証フロー全体は止めない（原則4: 非ブロッキング）。
    if (firebaseUser) {
      try {
        const result = await firebaseUser.getIdTokenResult()
        applyFromClaim(result.claims.accountType)
        applyOwnerFromClaim(result.claims.role)
      } catch (error) {
        console.error('[firebase] アカウント種別クレームの取得に失敗しました:', error)
        applyFromClaim(undefined)
        applyOwnerFromClaim(undefined)
      }
    } else {
      applyFromClaim(undefined)
      applyOwnerFromClaim(undefined)
    }
    authReady.value = true
  })

  return { provide: { firebaseAuth: auth } }
})

declare module '#app' {
  interface NuxtApp {
    $firebaseAuth: Auth | null
  }
}
