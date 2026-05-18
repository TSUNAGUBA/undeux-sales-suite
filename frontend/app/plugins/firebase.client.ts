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

  // Firebase 未設定時もアプリが停止しないよう、準備完了として扱う。
  if (!firebase.apiKey) {
    authReady.value = true
    return { provide: { firebaseAuth: null as Auth | null } }
  }

  const app = initializeApp({
    apiKey: firebase.apiKey,
    authDomain: firebase.authDomain,
    projectId: firebase.projectId,
  })
  const auth = getAuth(app)

  onAuthStateChanged(auth, (firebaseUser) => {
    user.value = firebaseUser
      ? { uid: firebaseUser.uid, email: firebaseUser.email }
      : null
    authReady.value = true
  })

  return { provide: { firebaseAuth: auth } }
})

declare module '#app' {
  interface NuxtApp {
    $firebaseAuth: Auth | null
  }
}
