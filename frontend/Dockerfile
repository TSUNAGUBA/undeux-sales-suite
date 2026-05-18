# ============================================================
#  UndeuxSales フロントエンド Dockerfile
#  Nuxt SPA を静的生成し、nginx で配信する。
#  Firebase / API の設定はビルド時引数（ssr:false のため埋め込み）。
# ============================================================

FROM node:22-alpine AS build
WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci

COPY . .

ARG NUXT_PUBLIC_API_BASE_URL=http://localhost:8080
ARG NUXT_PUBLIC_FIREBASE_API_KEY=
ARG NUXT_PUBLIC_FIREBASE_AUTH_DOMAIN=
ARG NUXT_PUBLIC_FIREBASE_PROJECT_ID=
ENV NUXT_PUBLIC_API_BASE_URL=$NUXT_PUBLIC_API_BASE_URL \
    NUXT_PUBLIC_FIREBASE_API_KEY=$NUXT_PUBLIC_FIREBASE_API_KEY \
    NUXT_PUBLIC_FIREBASE_AUTH_DOMAIN=$NUXT_PUBLIC_FIREBASE_AUTH_DOMAIN \
    NUXT_PUBLIC_FIREBASE_PROJECT_ID=$NUXT_PUBLIC_FIREBASE_PROJECT_ID

RUN npm run generate

# ---- 配信（nginx） ----
FROM nginx:1.27-alpine AS runtime
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/.output/public /usr/share/nginx/html
EXPOSE 80
