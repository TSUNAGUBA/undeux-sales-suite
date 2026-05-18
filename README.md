# UndeuxSales 売上参照スイート

小売（取引先）から週次で提供される売上参照データを PostgreSQL に格納し、
売上を可視化する Web アプリケーション。

- **全社サマリー** — 主要KPI（売上金額・数量・粗利・消化率）と週次トレンド
- **売上分析** — 日次／週次トレンド、部門・取引先・業態・季節・商品・カラー・サイズ別ランキング
- **商品別分析** — 品番・単品別の売上／在庫一覧（売れ筋・死に筋）
- **在庫・発注分析** — 在庫数・発注数・先付数・消化率・在庫日数
- **週次取込** — 週次CSVのアップロード取込と取込履歴

## 技術スタック

| 層 | 技術 |
|----|------|
| フロントエンド | Nuxt 4 / Vue 3 / TypeScript / Tailwind CSS v4 / lucide / Chart.js |
| バックエンド | C# (.NET 8 / ASP.NET Core) / Npgsql / Dapper |
| データベース | PostgreSQL 16 |
| 認証 | Firebase Authentication |
| インフラ | Firebase Hosting / AWS EC2 / AWS RDS |

## クイックスタート（Docker Compose）

```bash
# 1. 環境変数を用意（Firebase 設定を記入）
cp .env.example .env

# 2. 起動（DB 構築 → 初期データ投入 → API → フロントエンド）
docker compose up --build
```

| サービス | URL |
|---------|-----|
| フロントエンド | http://localhost:3000 |
| API | http://localhost:8080 |
| API ドキュメント（Swagger） | http://localhost:8080/swagger |

初回起動時、`refference/` の初期DBダンプ（約160万行）が自動投入される（数分かかる場合がある）。

> **ログインには Firebase の設定が必要です。** Firebase コンソールでプロジェクトを作成し、
> Authentication（メール/パスワード）を有効化のうえ、`.env` に各値を設定してください。
> 未設定でもアプリは起動しますが、ログインはできません。

## プロジェクト構成

```
undeux-sales-suite/
├── backend/            C# ソリューション
│   ├── src/UndeuxSales.Core/            ドメイン・週次日付ロジック・パーサ
│   ├── src/UndeuxSales.Infrastructure/  データアクセス・取込・分析クエリ
│   ├── src/UndeuxSales.Api/             ASP.NET Core Web API
│   ├── src/UndeuxSales.DataLoader/      初期DBダンプ投入ツール
│   └── tests/UndeuxSales.Tests/         ユニット・統合テスト
├── frontend/           Nuxt SPA
├── db/schema.sql       PostgreSQL スキーマDDL
├── refference/         小売提供の初期データ（DBダンプ・カラム定義）
├── infra/              デプロイ手順（Firebase / AWS）
├── docs/design.md      設計ドキュメント
└── docker-compose.yml
```

## ローカル開発

### バックエンド

```bash
cd backend
dotnet test                              # ユニット・統合テスト（PostgreSQL が必要）
dotnet run --project src/UndeuxSales.Api  # API 単体起動
```

統合テストは PostgreSQL（既定 `localhost:5432`、ユーザー/パスワード `undeux`）に接続し、
テスト用DB `undeux_test` を自動作成する。

### フロントエンド

```bash
cd frontend
npm install
npm run dev        # 開発サーバー（http://localhost:3000）
npm run generate   # 静的SPAを生成（.output/public）
npm run typecheck  # 型チェック
```

## 週次CSV取込フォーマット

- 文字コード **UTF-8**、1行目はヘッダー（列名）。
- `import_date` は **月曜日**（取込日。前週 月〜日のデータを表す）。
- 必須列: `import_date, customer_code, gyotai_code, chohyo_kubun_name, department,
  hinban_code, tanpin_code, hinmei, shohin_kigou, color, size,
  toshu_uriage_count1`〜`7, uriage_count_zenshu, uriage_count_2shumae`〜`4shumae,
  zaikosu, ruikei_uriage_count, ruikei_nohin_count, hatchu_count, donyu_date,
  zainiti, genka, baika, kisetsu, sakizuke_count`（任意列: `tanawari1, tanawari2`）。
- アプリの「週次取込」画面からテンプレートCSVをダウンロードできる。
- エラー行が1件でもある場合、取込は実行されない（修正して再アップロード）。

## デプロイ

`infra/README.md`（Firebase Hosting）および `infra/aws/README.md`（EC2 + RDS）を参照。

## 設計ドキュメント

アーキテクチャ・データモデル・API仕様は `docs/design.md` を参照。

## 開発方法論

本リポジトリは `.ai-native/` の AI ネイティブ開発方法論に基づいて開発されている。
方法論の詳細は `.ai-native/methodology/INDEX.md` を参照。
