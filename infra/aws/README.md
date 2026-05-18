# AWS デプロイ手順（EC2 + RDS）

C# Web API を EC2、PostgreSQL を RDS で稼働させる手順。

## 1. RDS for PostgreSQL

1. RDS で PostgreSQL 16 インスタンスを作成する。
   - 初期データベース名: `undeux`
   - マスターユーザー: `undeux`
2. セキュリティグループで、EC2 のセキュリティグループからの 5432/tcp 受信を許可する。
   （パブリックアクセスは無効のままとし、EC2 経由のみ接続可能にする）
3. 作成後、エンドポイント（ホスト名）を控える。

> スキーマDDL（`db/schema.sql`）は DataLoader 実行時に自動適用される。手動適用は不要。

## 2. EC2（API ホスト）

1. EC2 インスタンス（Amazon Linux 2023 等）を起動する。
2. Docker を導入する。

   ```bash
   sudo dnf install -y docker
   sudo systemctl enable --now docker
   sudo usermod -aG docker ec2-user
   ```

3. セキュリティグループ:
   - 22/tcp（SSH、管理元IPのみ）
   - 443/tcp（HTTPS。リバースプロキシ経由で API を公開する場合）

## 3. アプリケーションの配置

リポジトリを EC2 に配置し、バックエンドのイメージをビルドする。

```bash
# API イメージ
docker build -t undeux-api --target api ./backend
# DataLoader イメージ
docker build -t undeux-dataloader --target dataloader ./backend
```

### 初期データ投入（DataLoader を1回実行）

```bash
docker run --rm \
  -e ConnectionStrings__Default="Host=<RDSエンドポイント>;Port=5432;Database=undeux;Username=undeux;Password=<パスワード>;Command Timeout=600" \
  -e UNDEUX_DUMP_PATH=/refference \
  -e UNDEUX_SCHEMA_PATH=/app/db/schema.sql \
  -v "$(pwd)/refference:/refference:ro" \
  -v "$(pwd)/db:/app/db:ro" \
  undeux-dataloader
```

冪等のため、再実行しても二重投入されない（強制再投入は `UNDEUX_FORCE_RELOAD=true`）。

### API の起動

```bash
docker run -d --name undeux-api --restart unless-stopped \
  -p 8080:8080 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e ConnectionStrings__Default="Host=<RDSエンドポイント>;Port=5432;Database=undeux;Username=undeux;Password=<パスワード>" \
  -e Firebase__ProjectId="<Firebaseプロジェクト ID>" \
  -e Cors__AllowedOrigins__0="https://<Firebase Hosting のドメイン>" \
  undeux-api
```

## 4. HTTPS 化（必須）

フロントエンド（Firebase Hosting）は HTTPS 配信のため、API も HTTPS で公開する必要がある
（HTTP のままだとブラウザの混在コンテンツブロックで API 呼び出しが失敗する）。いずれかを採用する。

- **ALB + ACM:** Application Load Balancer に ACM 証明書を割り当て、EC2:8080 へ転送する。
- **リバースプロキシ:** EC2 上に nginx + Let's Encrypt を構成し、443→8080 へ転送する。

`Cors__AllowedOrigins__0` には Firebase Hosting の本番ドメインを設定する。

## 5. 環境変数一覧

| 変数 | 説明 |
|------|------|
| `ConnectionStrings__Default` | RDS への接続文字列 |
| `Firebase__ProjectId` | Firebase プロジェクトID（IDトークン検証） |
| `Cors__AllowedOrigins__0` | 許可するフロントエンドのオリジン |
| `ASPNETCORE_ENVIRONMENT` | `Production`（本番） |

## 監視

- ヘルスチェック: `GET /api/health`（稼働確認）、`GET /api/health/ready`（DB接続込み）。
  ALB のターゲットグループのヘルスチェックパスに `/api/health/ready` を設定する。
