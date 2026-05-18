#!/usr/bin/env bash
# ============================================================
#  EC2 上で API をビルド・デプロイする。
#  GitHub Actions（deploy-backend）から SSH 経由で実行される。
#  手動実行も可能: bash ~/undeux-sales-suite/infra/aws/deploy-ec2.sh
# ============================================================
set -euo pipefail

cd "$(dirname "$0")"

if [ ! -f .env ]; then
  echo "エラー: .env が見つかりません（infra/aws/.env）。" >&2
  exit 1
fi

# 接続文字列等の機密値を含むため、所有者のみ読み取り可能にする。
chmod 600 .env

compose=(docker compose -f docker-compose.ec2.yml --env-file .env)

echo "==> イメージをビルド"
"${compose[@]}" build

echo "==> 初期データ投入（冪等: 投入済みならスキップ）"
"${compose[@]}" run --rm dataloader

echo "==> API・リバースプロキシを起動（API が正常になるまで待機）"
"${compose[@]}" up -d --wait --wait-timeout 180 api caddy

echo "==> 不要になったイメージを削除"
docker image prune -f

echo "==> デプロイ完了"
