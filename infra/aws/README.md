# AWS（EC2 + RDS）— 構成と運用リファレンス

バックエンドAPIを EC2、PostgreSQL を RDS で稼働させる。

## セットアップ・デプロイ

初回セットアップ（Firebase / RDS / EC2 の構築、シークレット登録）と GitHub Actions に
よる自動デプロイの手順は **`../deploy-guide.md`** を参照（PowerShell コマンドベース）。

EC2 上の構成:

| 要素 | 内容 |
|------|------|
| `caddy` | 自動HTTPS リバースプロキシ（Let's Encrypt）。80/443 を公開し `api:8080` へ転送 |
| `api` | C# Web API コンテナ |
| `dataloader` | 初期DBダンプを RDS へ投入（冪等。初回のみ実投入） |
| 定義 | `docker-compose.ec2.yml` / `Caddyfile` / `deploy-ec2.sh` |
| 設定 | `infra/aws/.env`（GitHub Actions が `deploy-backend` 実行時に生成） |

データベースは AWS RDS（PostgreSQL 16）。スキーマDDL（`db/schema.sql`）は
DataLoader 実行時に自動適用される。

## EC2 上での手動運用コマンド

EC2 へ SSH 接続して操作する。

```bash
ssh -i ~/.ssh/undeux-ec2 ubuntu@<EC2のIP>
cd ~/undeux-sales-suite/infra/aws
```

| 操作 | コマンド |
|------|---------|
| 手動デプロイ | `bash deploy-ec2.sh` |
| APIログ表示 | `docker compose -f docker-compose.ec2.yml --env-file .env logs -f api` |
| 再起動 | `docker compose -f docker-compose.ec2.yml --env-file .env restart api` |
| 状態確認 | `docker compose -f docker-compose.ec2.yml --env-file .env ps` |
| データ強制再投入 | `UNDEUX_FORCE_RELOAD=true` を付けて dataloader を実行 |

## 環境変数

`infra/aws/.env`（自動生成）に格納される。`docker-compose.ec2.yml` が参照する。

| 変数 | 説明 |
|------|------|
| `UNDEUX_DB_CONNECTION` | RDS への接続文字列 |
| `UNDEUX_FIREBASE_PROJECT_ID` | Firebase プロジェクトID（IDトークン検証） |
| `UNDEUX_FRONTEND_ORIGIN` | 許可するフロントエンドのオリジン（CORS） |
| `UNDEUX_API_DOMAIN` | API のドメイン名（Caddy の証明書取得に使用） |

## 監視

- **ヘルスチェック:** `GET /api/health`（稼働確認）、`GET /api/health/ready`（DB接続込み）。
- **メトリクス:** CloudWatch で以下を収集し、しきい値アラート（SNS 通知）を設定することを推奨する。
  - EC2: CPU 使用率・メモリ・ステータスチェック
  - RDS: CPU・接続数・空きストレージ
- **ログ:** API は標準出力に構造化ログを出力する。CloudWatch Logs エージェントで
  集約し、エラーコード（`UNDX-xxx-nnn`）や `fail` レベルで検索・アラートする。
- **リソース上限:** 各コンテナに CPU/メモリ上限を設定済み（`docker-compose.ec2.yml`）。
- **接続プール:** API は1リクエストにつきDB接続1本を使用する。接続文字列に
  `Maximum Pool Size` を明示し、RDS の `max_connections` を超えないよう設計する。
- 取込障害は `import_batch` テーブルの `status='failed'` 行で追跡できる。

## セキュリティ上の注意

- EC2 のセキュリティグループは 22/80/443 を開放するが、SSH は鍵認証のみ（パスワード認証無効）。
  本番でアクセス元を限定する場合は、22番ポートを管理元IP・CIの固定IPに絞るか、
  AWS Systems Manager Session Manager の利用を検討する。
