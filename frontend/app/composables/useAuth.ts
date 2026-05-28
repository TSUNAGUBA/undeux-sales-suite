import { signInWithEmailAndPassword, signOut } from 'firebase/auth'
import type { AuthUser } from '~/types/api'

/** 認証状態とログイン・ログアウト操作を提供するコンポーザブル。 */
export function useAuth() {
  const user = useState<AuthUser | null>('auth-user', () => null)
  const authReady = useState<boolean>('auth-ready', () => false)
  const { $firebaseAuth } = useNuxtApp()

  const isAuthenticated = computed(() => user.value !== null)
  const isConfigured = computed(() => $firebaseAuth !== null)

  /** 認証状態の初期判定が完了するまで待機する。 */
  function ensureReady(): Promise<void> {
    if (authReady.value) {
      return Promise.resolve()
    }
    return new Promise((resolve) => {
      const stop = watch(authReady, (ready) => {
        if (ready) {
          stop()
          resolve()
        }
      })
    })
  }

  async function login(email: string, password: string): Promise<void> {
    if (!$firebaseAuth) {
      throw new Error('Firebase認証が設定されていません。管理者に連絡してください。')
    }
    await signInWithEmailAndPassword($firebaseAuth, email, password)
  }

  async function logout(): Promise<void> {
    if ($firebaseAuth) {
      await signOut($firebaseAuth)
    }
    // 共有端末で前ユーザーのサイドバー開閉設定が引き継がれないようにクリアする。
    useSidebar().clearStored()
  }

  /** APIリクエスト用のFirebase IDトークンを取得する。未認証時は null。 */
  async function getIdToken(): Promise<string | null> {
    const current = $firebaseAuth?.currentUser
    return current ? await current.getIdToken() : null
  }

  return {
    user,
    authReady,
    isAuthenticated,
    isConfigured,
    ensureReady,
    login,
    logout,
    getIdToken,
  }
}
