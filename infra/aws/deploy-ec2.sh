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

echo "==> ビルド前クリーンアップ（ビルドキャッシュ・未使用イメージの削除）"
# イメージビルドのキャッシュ（containerd スナップショット）はデプロイ世代ごとに蓄積し、
# 放置するとディスクを使い切ってビルド自体が失敗する（2026-06-12 の実障害:
# "no space left on device"）。ビルド「後」だけの掃除では失敗時に実行されず容量も
# 回復しない自己回復不能の構造になるため、必ずビルド「前」に行う。
# 全キャッシュ削除によりビルドが毎回フル実行になるが（数分程度の増）、ディスク使用量が
# 世代数に依らず有界になることを優先する。
# 補助処理のため失敗してもデプロイは継続する（|| true）。
# 注意: --volumes は付けないこと（永続データの保護。DB は RDS だが防御として維持）。
docker builder prune -af || true
docker image prune -f || true
echo "    ディスク使用状況: $(df -h / | tail -1)"

echo "==> イメージをビルド"
"${compose[@]}" build

echo "==> 初期データ投入（冪等: 投入済みならスキップ）"
"${compose[@]}" run --rm dataloader

echo "==> API を起動（EC2 上の nginx-proxy 経由で公開。API が正常になるまで待機）"
"${compose[@]}" up -d --wait --wait-timeout 180 --remove-orphans api

echo "==> 不要になったイメージを削除"
# 旧世代のイメージは新イメージ起動後に dangling になるため、ここでも掃除する
# （ビルド前クリーンアップと両輪。こちらは成功時のみ実行される）。
docker image prune -f

echo "==> デプロイ完了"
