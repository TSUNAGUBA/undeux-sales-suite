#!/usr/bin/env bash
# ============================================================
#  EC2 上で API を pull・デプロイする。
#  イメージのビルドは CI（deploy-backend ワークフロー）で行い GHCR へ push 済み。
#  本スクリプトは pull + 初期データ投入 + 起動のみを担う（EC2 に .NET SDK は不要）。
#  GitHub Actions（deploy-backend）から SSH 経由で実行される。GHCR へは事前に
#  docker login 済みであること（ワークフローが --password-stdin でログインする）。
#  手動実行も可能: docker login ghcr.io した上で bash ~/undeux-sales-suite/infra/aws/deploy-ec2.sh
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

# イメージは CI（deploy-backend ワークフロー）で GHCR にビルド済み。EC2 上ではビルドせず pull のみ。
# これにより EC2 に .NET SDK（約1.5GB展開）を載せずに済み、ルートディスク枯渇
# （2026-06-12 / 2026-07-01 の "no space left on device"）を恒久的に回避する。
echo "==> プル前クリーンアップ（未使用イメージ・キャッシュの削除）"
# 旧世代の pull 済みイメージは新タグへの入替で dangling になり蓄積するため、pull 前に掃除して
# ディスク使用量を世代数に依らず有界に保つ。補助処理のため失敗してもデプロイは継続する（|| true）。
# 注意: --volumes は付けないこと（永続データの保護。DB は RDS だが防御として維持）。
echo "    ディスク使用状況（クリーンアップ前）: $(df -h / | tail -1)"
docker builder prune -af || true
docker image prune -f || true
echo "    ディスク使用状況（クリーンアップ後）: $(df -h / | tail -1)"

echo "==> イメージを pull（GHCR）"
"${compose[@]}" pull

echo "==> 初期データ投入（冪等: 投入済みならスキップ）"
"${compose[@]}" run --rm dataloader

echo "==> API を起動（EC2 上の nginx-proxy 経由で公開。API が正常になるまで待機）"
"${compose[@]}" up -d --wait --wait-timeout 180 --remove-orphans api

echo "==> 不要になったイメージを削除"
# 旧世代のイメージは pull で新タグへ入れ替わり dangling になるため、起動成功後にも掃除する
# （プル前クリーンアップと両輪）。補助処理のため失敗してもデプロイ成功を覆さない
# （|| true がないと set -e で非零終了し、deploy パイプラインが backend 失敗としてフロント配信を偽陽性で中止する）。
docker image prune -f || true

# GHCR の認証情報を EC2 に残さない（ワークフローが渡す一時トークンのため logout する）。
docker logout ghcr.io || true

echo "==> デプロイ完了"
