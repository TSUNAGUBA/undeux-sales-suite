# UndeuxSales 売上参照スイート

小売（取引先）から週次で提供される売上参照データを PostgreSQL に格納し、
売上を可視化する Web アプリケーション。

分析画面は分析用ディメンショナルモデル（`mart` スキーマ＝スタースキーマ。設計: `docs/star-schema-design.md`）
から集計する `/mart` 配下のページ群（プロトタイプ段階の旧分析ページは廃止済み）。

ナビゲーションは目的別ドリルダウン構成（メニュー定義の SoT: `frontend/app/utils/navigation.ts`）。
`/`（ホーム）で目的カテゴリを選び、配下ページをタブで切り替える。表示メニューは**アカウント種別**で出し分ける（後述）。

- **OTB管理**（バイヤー＝小売のみ） — 全社OTBサマリー
- **販売モニタリング**（サプライヤー＝メーカー） — 全社サマリー／売上分析
- **週間モニタリング**（サプライヤー） — 直近週の実績・前週比・週次推移
- **在庫マネジメント**（サプライヤー） — アクション駆動の在庫管理
- **ブランド/シリーズ分析**（サプライヤー） — ブランド・シリーズ（商品記号）軸の集計
- **アイテム分析**（サプライヤー） — 商品別分析／商品導入管理
- **探索・予測分析** — クロス集計／ランキング分析／散布図・回帰分析／重回帰シミュレーター
- **データ管理** — 予算管理／商品マスタ／週次取込（商品マスタ・週次取込はサプライヤーのみ）

### アカウント種別（サプライヤー／バイヤー）

利用者は **サプライヤー（メーカー）** と **バイヤー（小売）** のいずれかのアカウント種別を持つ
（Firebase カスタムクレーム `accountType`。未設定の既定は `supplier`）。種別はメニュー・タブ・ルートを出し分ける。

- **サプライヤー（メーカー）**: 自社の売上・在庫の管理/分析（既存の「売れた結果を見る」画面群）。
- **バイヤー（小売）**: 全サプライヤー横断で仕入予算・OTB（Open To Buy＝仕入枠）を管理し、「未来の仕入意思決定」を行う。

> **重要（運用上の制約）:**
> - 現状のロール出し分けは**ナビゲーション/画面レベル**であり、バックエンドのデータ分離（メーカーは自社商品のみ）は
>   マルチベンダ対応として後続。**セキュリティ境界ではない**（API はトークンが有効なら現状すべてのデータを返す）。
> - 開発/デモ用にヘッダーでアカウント種別を手動切替できる（localStorage 上書き）。実運用ではクレームを SoT とする。

- **全社OTBサマリー**（`/mart/otb`・バイヤー向け） — 「未来の仕入意思決定」をする OTB（Open To Buy）ダッシュボード。OTB残高・利用率・発注残・予測月末在庫・WOS・欠品/過剰SKU数・平均リードタイム等のKPI、強み/弱み/機会/リスク、今週の推奨アクション、OTB構成ウォーターフォール、週次推移、カテゴリ別OTBランキング、発注残分析（未出荷/輸送中/検収済）、AIコメントを表示。**現状は数値・所見をルールベースで生成するモック**（AI本格導入は後続。SoT: `frontend/app/utils/otbMock.ts`）。色は青=OTB・発注余力／緑=健全／黄=注意／赤=欠品・過剰在庫・納期遅延。目標売上・仕入予算は予算管理の登録値を反映
- **全社サマリー**（`/mart`） — AIレポート風の経営サマリー。業態タブ＋部門チップ（各「すべて」）で絞り込み、主要KPI（前年同期比つき）・週次トレンド・「今週のアクション（在庫）」ダイジェスト・集計軸別の売上構成に加え、主要指標からルールベースで自動生成するエグゼクティブサマリー（要点＋強み/弱み/機会/リスク）を表示。mart の再構築も実行可
- **売上分析**（`/mart/sales`） — 週次売上推移グラフ（売上数量/売上金額=折れ線、店頭在庫=棒、気温=折れ線）・週次明細・集計軸別の売上構成・順位変動（前年同期比）。期間は年月の from-to で指定
- **週間モニタリング**（`/mart/weekly`） — 直近取込週の実績と前週比（WoW）、週次推移グラフ・前週比つき週次明細
- **商品別分析**（`/mart/products`） — 画像カードの商品一覧。フィルターは全社サマリー踏襲（業態・部門・年度・季節・棚割1・平均在庫日数）＋ブランド・担当者・キーワード。カード押下で商品の詳細分析（画像・基本情報・サマリー・SKU情報・週次売上推移グラフ・クロス集計）へ
- **在庫マネジメント**（`/mart/inventory`） — アクション駆動の在庫管理。ページ内4タブ（ダッシュボード／在庫一覧／滞留在庫／不動在庫、`?tab=` で直リンク可）。滞留（在庫日数60日超×消化率75%未満）・不動（直近8週出荷ゼロ）を SKU 単位で自動抽出し、「今週のアクション」フィード・推奨アクション（発注抑制／値下げ候補／売場再点検／処分検討／経過観察）・部門ポジショニング4象限・在庫鮮度帯・在庫金額（原価）を提供。閾値の SoT はバックエンド `InventoryHealthRules`（適用値は API レスポンスで返却）
- **ブランド/シリーズ分析**（`/mart/brand`） — ブランド軸・シリーズ（商品記号）軸の売上ランキング・構成比（指標: 売上金額／数量／粗利。シリーズはデータに無いため商品記号で代替）
- **クロス集計**（`/mart/crosstab`） — 行×列の2軸マトリクス（複数集計値の表示モード3種・行列スワップ・気温オーバーレイ・Excel貼り付け用クリップボードコピー）
- **ランキング分析**（`/mart/ranking`） — 単軸ランキングに「複合スコアリング」「期間比較・順位変動」「ABC/パレート分析」の3レンズを統合
- **散布図・回帰分析**（`/mart/scatter`） — 「気温×売上数量」の単回帰でスイッチ温度を、「消化率×値引き率」の4象限＋型番別明細で値下げ判断を可視化
- **重回帰シミュレーター**（`/mart/simulation`） — 気温・前週売上・値引き率のスライダーで売上・粗利をリアルタイム予測（裏側は重回帰＋価格弾力性）
- **商品導入管理**（`/mart/introductions`） — 商品単位の導入日一覧。業態（タグ・複数選択）・部門・ブランド・服種・担当者・キーワード・導入時期/導入日 From-To で絞り込み
- **予算管理**（`/mart/budget`） — 売上予算（両ロール）・仕入予算（バイヤーのみ）を年度×集計軸（全社／部門／業態）で登録。登録値は OTB サマリー等で活用。**永続化は当面ブラウザの localStorage（端末・ユーザー間で共有されない暫定実装。バックエンドの予算テーブル連携は後続）**
- **商品マスタ**（`/product-master`） — マスタ登録商品のカード一覧・詳細（データ管理）
- **週次取込**（`/imports`） — 週次CSVのアップロード取込と取込履歴

フィルタは「フィルタ → 集計単位 → 表示集計値」の導線で統一。業態・部門は全社サマリーを標準とした
タグ（`ScopeFilterTags`・複数選択。表記は業態 `{コード}: {名称}({略称})`／部門 `{コード}: {名称}`）で
共通実装し、共通フィルタに **棚割1** と **平均在庫日数（在日）**（◎30日以内／〇31〜60日／△61日以上）を持つ。
気温は `db/climate_daily.csv`（東京/札幌/那覇の日次実測）を `mart.dim_climate` に投入して用い、
CSV 未カバーの週は標準気候（気象庁平年値ベースのサンプルデータ。標準＝東京・寒冷＝札幌・温暖＝那覇）へ
フォールバックする。mart は `sales_weekly`（取込ソース層・SoT）＋商品マスタから再構築する派生キャッシュで、
帳票区分・棚割は集計軸としては未対応（棚割1はフィルタ対応済み）。

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
- 取込は **取込権限ロール**（Firebase カスタムクレーム `role=admin`）を持つ利用者のみ実行可能。
  権限のない利用者は参照のみ（詳細は `infra/README.md`）。

## デプロイ

GitHub Actions による自動デプロイ（フロント→Firebase Hosting、API→AWS EC2）を構成済み。
一括デプロイは `deploy-all` ワークフロー（バックエンド → フロントエンドの順に直列実行）、
個別デプロイは従来どおり `deploy-backend` / `deploy-frontend` を使用する。
初回セットアップとデプロイの全手順（PowerShell コマンドベース）は
`infra/deploy-guide.md` を参照。構成の概要は `infra/README.md`。

## 設計ドキュメント

アーキテクチャ・データモデル・API仕様は `docs/design.md` を参照。

## 開発方法論

本リポジトリは `.ai-native/` の AI ネイティブ開発方法論に基づいて開発されている。
方法論の詳細は `.ai-native/methodology/INDEX.md` を参照。
