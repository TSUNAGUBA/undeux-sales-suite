<script setup lang="ts">
import { LogIn, TriangleAlert } from 'lucide-vue-next'

definePageMeta({ layout: 'auth' })
useHead({ title: 'ログイン | UndeuxSales' })

const { login, isConfigured } = useAuth()
const router = useRouter()

const email = ref('')
const password = ref('')
const errorMessage = ref<string | null>(null)
const submitting = ref(false)

function firebaseAuthMessage(error: unknown): string {
  const code = (error as { code?: string })?.code
  switch (code) {
    case 'auth/invalid-email':
      return 'メールアドレスの形式が正しくありません。'
    case 'auth/invalid-credential':
    case 'auth/wrong-password':
    case 'auth/user-not-found':
      return 'メールアドレスまたはパスワードが正しくありません。'
    case 'auth/too-many-requests':
      return '試行回数が上限を超えました。しばらく待って再度お試しください。'
    case 'auth/network-request-failed':
      return 'ネットワークエラーが発生しました。接続を確認してください。'
    default:
      return error instanceof Error ? error.message : 'ログインに失敗しました。'
  }
}

async function handleSubmit(): Promise<void> {
  errorMessage.value = null
  submitting.value = true
  try {
    await login(email.value, password.value)
    await router.push('/')
  } catch (error) {
    errorMessage.value = firebaseAuthMessage(error)
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <div class="w-full max-w-sm rounded-2xl bg-white p-8 shadow-xl">
    <div class="mb-6 text-center">
      <h1 class="text-xl font-bold text-slate-800">UndeuxSales</h1>
      <p class="text-sm text-slate-500">売上参照スイート</p>
    </div>

    <div
      v-if="!isConfigured"
      class="mb-4 flex gap-2 rounded-lg bg-amber-50 p-3 text-xs text-amber-700"
    >
      <TriangleAlert class="h-4 w-4 shrink-0" />
      <span>
        Firebase認証が未設定です。環境変数 NUXT_PUBLIC_FIREBASE_* を設定してください。
      </span>
    </div>

    <form class="space-y-4" @submit.prevent="handleSubmit">
      <div>
        <label for="email" class="mb-1 block text-sm font-medium text-slate-600">
          メールアドレス
        </label>
        <input
          id="email"
          v-model="email"
          type="email"
          required
          autocomplete="email"
          class="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none"
        >
      </div>

      <div>
        <label for="password" class="mb-1 block text-sm font-medium text-slate-600">
          パスワード
        </label>
        <input
          id="password"
          v-model="password"
          type="password"
          required
          autocomplete="current-password"
          class="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none"
        >
      </div>

      <p v-if="errorMessage" class="text-sm text-red-600">{{ errorMessage }}</p>

      <button
        type="submit"
        :disabled="submitting"
        class="flex w-full items-center justify-center gap-2 rounded-lg bg-indigo-600 px-4 py-2.5 text-sm font-medium text-white hover:bg-indigo-700 disabled:opacity-60"
      >
        <LogIn class="h-4 w-4" />
        {{ submitting ? 'ログイン中...' : 'ログイン' }}
      </button>
    </form>
  </div>
</template>
