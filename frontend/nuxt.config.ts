import tailwindcss from '@tailwindcss/vite'

// UndeuxSales 売上参照スイート — フロントエンド設定
// SPA（ssr: false）として Firebase Hosting へ静的配信する。
export default defineNuxtConfig({
  compatibilityDate: '2025-05-01',
  ssr: false,
  devtools: { enabled: false },

  css: ['~/assets/css/main.css'],

  vite: {
    plugins: [tailwindcss()],
  },

  // 公開ランタイム設定。NUXT_PUBLIC_* 環境変数でビルド時に上書きする。
  runtimeConfig: {
    public: {
      apiBaseUrl: 'http://localhost:8080',
      firebase: {
        apiKey: '',
        authDomain: '',
        projectId: '',
      },
    },
  },

  app: {
    head: {
      title: 'UndeuxSales 売上参照',
      htmlAttrs: { lang: 'ja' },
      meta: [
        { charset: 'utf-8' },
        { name: 'viewport', content: 'width=device-width, initial-scale=1' },
        { name: 'description', content: '小売提供の週次売上参照データを可視化するダッシュボード' },
      ],
    },
  },
})
