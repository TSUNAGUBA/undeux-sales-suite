-- ============================================================
--  UndeuxSales 売上参照スイート — PostgreSQL スキーマ
-- ------------------------------------------------------------
--  小売から提供される週次売上参照ファイルを格納し、売上を
--  可視化するためのスキーマ。元データ（MariaDB `sales` テーブル）の
--  構造を踏襲しつつ、PostgreSQL 向けに正規化・整理した。
--
--  設計方針:
--   * ファクトテーブル `sales_weekly` は意味を持たない代理キー (id) を
--     主キーとする。業務複合キーは UPSERT 用の UNIQUE 制約に限定する。
--   * 取込履歴 `import_batch` は追記専用。取込済みデータの SoT。
--   * コードマスタ (department / customer / business_type / season) は
--     取込時に同一トランザクションで自動導出される派生データ。
--   * 文字列カラムは text を採用（元データの varchar 長は MySQL の
--     ストレージ都合であり業務制約ではないため）。意味は COMMENT で記録。
-- ============================================================

BEGIN;

-- ------------------------------------------------------------
-- 取込バッチ（ingestion audit log） — 取込済みデータの SoT
-- ------------------------------------------------------------
CREATE TABLE IF NOT EXISTS import_batch (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    source_type     text NOT NULL
                        CHECK (source_type IN ('initial_dump', 'weekly_csv')),
    file_name       text NOT NULL,
    status          text NOT NULL DEFAULT 'processing'
                        CHECK (status IN ('processing', 'completed', 'failed')),
    row_count       integer NOT NULL DEFAULT 0,
    week_count      integer NOT NULL DEFAULT 0,
    min_import_date date,
    max_import_date date,
    error_message   text,
    started_at      timestamptz NOT NULL DEFAULT now(),
    completed_at    timestamptz
);

COMMENT ON TABLE  import_batch IS '取込バッチ履歴。1回の取込（初期DB投入 / 週次CSV）を1行で記録する追記専用テーブル';
COMMENT ON COLUMN import_batch.source_type IS '取込種別: initial_dump=初期DBダンプ投入 / weekly_csv=週次CSV取込';
COMMENT ON COLUMN import_batch.status      IS '取込状態: processing / completed / failed';
COMMENT ON COLUMN import_batch.week_count  IS 'このバッチに含まれる import_date（週）の種類数';

-- ------------------------------------------------------------
-- 売上参照ファクト（週次スナップショット）
-- ------------------------------------------------------------
--  1行 = ある取込日(週)における、ある店舗・商品単品の売上スナップショット。
--  日次列 toshu_uriage_count1..7 は import_date の前週 月〜日に対応する
--  （import_date が月曜のとき、月曜列 = import_date-7、日曜列 = import_date-1）。
-- ------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sales_weekly (
    id                    bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    import_batch_id       bigint NOT NULL REFERENCES import_batch(id),

    import_date           date NOT NULL,
    customer_code         text NOT NULL,
    gyotai_code           text NOT NULL,
    chohyo_kubun_name     text NOT NULL,
    department            text NOT NULL,
    hinban_code           text NOT NULL,
    tanpin_code           text NOT NULL,
    hinmei                text NOT NULL,
    shohin_kigou          text NOT NULL,
    color                 text NOT NULL,
    size                  text NOT NULL,
    tanawari1             text,
    tanawari2             text,

    toshu_uriage_count1   integer NOT NULL DEFAULT 0,
    toshu_uriage_count2   integer NOT NULL DEFAULT 0,
    toshu_uriage_count3   integer NOT NULL DEFAULT 0,
    toshu_uriage_count4   integer NOT NULL DEFAULT 0,
    toshu_uriage_count5   integer NOT NULL DEFAULT 0,
    toshu_uriage_count6   integer NOT NULL DEFAULT 0,
    toshu_uriage_count7   integer NOT NULL DEFAULT 0,

    uriage_count_zenshu   integer NOT NULL DEFAULT 0,
    uriage_count_2shumae  integer NOT NULL DEFAULT 0,
    uriage_count_3shumae  integer NOT NULL DEFAULT 0,
    uriage_count_4shumae  integer NOT NULL DEFAULT 0,

    zaikosu               integer NOT NULL DEFAULT 0,
    ruikei_uriage_count   integer NOT NULL DEFAULT 0,
    ruikei_nohin_count    integer NOT NULL DEFAULT 0,
    hatchu_count          numeric(10,1) NOT NULL DEFAULT 0,

    donyu_date            text NOT NULL,
    zainiti               integer NOT NULL DEFAULT 0,
    genka                 integer NOT NULL DEFAULT 0,
    baika                 integer NOT NULL DEFAULT 0,
    kisetsu               text NOT NULL,
    sakizuke_count        integer NOT NULL DEFAULT 0,

    source_created_at     timestamp,
    source_updated_at     timestamp,
    ingested_at           timestamptz NOT NULL DEFAULT now()
);

COMMENT ON TABLE  sales_weekly IS '売上参照ファクト。週次スナップショット（取込日 × 店舗 × 商品単品）';
COMMENT ON COLUMN sales_weekly.import_date          IS '取込日（月曜日）。元データ varchar→date';
COMMENT ON COLUMN sales_weekly.customer_code        IS '取引先コード（店舗・取引アカウント）。元 varchar(4)';
COMMENT ON COLUMN sales_weekly.gyotai_code          IS '業態コード。元 varchar(2)';
COMMENT ON COLUMN sales_weekly.chohyo_kubun_name    IS '帳票区分名。元 varchar(4)';
COMMENT ON COLUMN sales_weekly.department           IS '部門コード。元 varchar(2)';
COMMENT ON COLUMN sales_weekly.hinban_code          IS '品番コード。元 varchar(3)';
COMMENT ON COLUMN sales_weekly.tanpin_code          IS '単品コード。元 varchar(4)';
COMMENT ON COLUMN sales_weekly.hinmei               IS '品名。元 varchar(15)';
COMMENT ON COLUMN sales_weekly.shohin_kigou         IS '商品記号。元 varchar(12)';
COMMENT ON COLUMN sales_weekly.color                IS 'カラー。元 varchar(15)';
COMMENT ON COLUMN sales_weekly.size                 IS 'サイズ。元 varchar(11)';
COMMENT ON COLUMN sales_weekly.tanawari1            IS '棚割1。元 varchar(6) NULL可';
COMMENT ON COLUMN sales_weekly.tanawari2            IS '棚割2。元 varchar(6) NULL可';
COMMENT ON COLUMN sales_weekly.toshu_uriage_count1  IS '当週売上数量（月） = import_date-7 の売上数量';
COMMENT ON COLUMN sales_weekly.toshu_uriage_count7  IS '当週売上数量（日） = import_date-1 の売上数量';
COMMENT ON COLUMN sales_weekly.uriage_count_zenshu  IS '前週売上数量（週合計）';
COMMENT ON COLUMN sales_weekly.uriage_count_2shumae IS '2週前売上数量（週合計）';
COMMENT ON COLUMN sales_weekly.zaikosu              IS '在庫数（マイナス値あり＝調整・過剰販売）';
COMMENT ON COLUMN sales_weekly.ruikei_uriage_count  IS '累計売上数';
COMMENT ON COLUMN sales_weekly.ruikei_nohin_count   IS '累計納品数';
COMMENT ON COLUMN sales_weekly.hatchu_count         IS '発注数。元 decimal(10,1)';
COMMENT ON COLUMN sales_weekly.donyu_date           IS '導入日（YYYYMMDD 文字列。"0"=未設定）。元 varchar(8)';
COMMENT ON COLUMN sales_weekly.zainiti              IS '在日（導入からの経過日数）';
COMMENT ON COLUMN sales_weekly.genka                IS '原価（円）';
COMMENT ON COLUMN sales_weekly.baika                IS '売価（円）';
COMMENT ON COLUMN sales_weekly.kisetsu              IS '季節区分（例: 通季）。元 varchar(4)';
COMMENT ON COLUMN sales_weekly.sakizuke_count       IS '先付数';
COMMENT ON COLUMN sales_weekly.source_created_at    IS '元データの created_at（取込前の値を保持）';
COMMENT ON COLUMN sales_weekly.ingested_at          IS '当システムへ取り込んだ日時';

-- 業務複合キー: 冪等な UPSERT（再取込時の重複防止）のための UNIQUE 制約。
-- リレーションには使用しない（リレーションは代理キー id を使用）。
CREATE UNIQUE INDEX IF NOT EXISTS ux_sales_weekly_business_key
    ON sales_weekly (import_date, customer_code, gyotai_code,
                     hinban_code, tanpin_code, shohin_kigou, donyu_date);

-- 分析クエリ用インデックス
CREATE INDEX IF NOT EXISTS ix_sales_weekly_import_date   ON sales_weekly (import_date);
CREATE INDEX IF NOT EXISTS ix_sales_weekly_department    ON sales_weekly (department);
CREATE INDEX IF NOT EXISTS ix_sales_weekly_customer_code ON sales_weekly (customer_code);
CREATE INDEX IF NOT EXISTS ix_sales_weekly_kisetsu       ON sales_weekly (kisetsu);
CREATE INDEX IF NOT EXISTS ix_sales_weekly_gyotai_code   ON sales_weekly (gyotai_code);
CREATE INDEX IF NOT EXISTS ix_sales_weekly_batch         ON sales_weekly (import_batch_id);

-- 商品（品番・単品）単位の集計・件数算出を高速化する。
CREATE INDEX IF NOT EXISTS ix_sales_weekly_product
    ON sales_weekly (hinban_code, tanpin_code);

-- 商品マスタとの自然キー（業態×記号×品番）結合・EXISTS 絞り込み（商品別分析の「全社サマリー踏襲」
-- フィルタ等）を高速化する。末尾の import_date で期間レンジ条件もインデックスでカバーする。
CREATE INDEX IF NOT EXISTS ix_sales_weekly_natural_key
    ON sales_weekly (gyotai_code, shohin_kigou, hinban_code, import_date);

-- ------------------------------------------------------------
-- コードマスタ（取込時に自動導出される派生参照データ）
-- ------------------------------------------------------------
--  フィルタUIの選択肢として利用する（review-standards LAYER_1 1.3）。
--  SoT は売上参照ファイル。取込時に同一トランザクションで UPSERT され、
--  ファクトと原子的に整合する。display_name は将来の表示名付与用（任意）。
-- ------------------------------------------------------------
CREATE TABLE IF NOT EXISTS department (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code         text NOT NULL UNIQUE,
    display_name text,
    created_at   timestamptz NOT NULL DEFAULT now()
);
COMMENT ON TABLE department IS '部門コードマスタ（取込時に自動導出）';

CREATE TABLE IF NOT EXISTS customer (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code         text NOT NULL UNIQUE,
    display_name text,
    created_at   timestamptz NOT NULL DEFAULT now()
);
COMMENT ON TABLE customer IS '取引先コードマスタ（取込時に自動導出）';

CREATE TABLE IF NOT EXISTS business_type (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code         text NOT NULL UNIQUE,
    display_name text,
    created_at   timestamptz NOT NULL DEFAULT now()
);
COMMENT ON TABLE business_type IS '業態コードマスタ（取込時に自動導出）';

CREATE TABLE IF NOT EXISTS season (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code         text NOT NULL UNIQUE,
    display_name text,
    created_at   timestamptz NOT NULL DEFAULT now()
);
COMMENT ON TABLE season IS '季節区分マスタ（取込時に自動導出）';

-- ------------------------------------------------------------
-- 業態マスタの拡張: short_name 列を追加し、業態名（display_name）と
-- 略称（short_name）を保守できるようにする。SoTは依然として sales_weekly
-- だが、コード単独では UI 表示が読みにくいため、運用側で代表名を付与する。
-- 取込時の自動 UPSERT（INSERT ... ON CONFLICT DO NOTHING）は code 行のみ
-- 挿入し、display_name / short_name は上書きしないため運用設定が温存される。
-- ------------------------------------------------------------
ALTER TABLE business_type
    ADD COLUMN IF NOT EXISTS short_name text;
COMMENT ON COLUMN business_type.short_name IS '業態の英数略称（例: sm=しまむら, av=アベイル）';

-- 業態マスタの代表データ（01-06）。display_name / short_name は運用者が編集する
-- 設定値であり、毎回の上書きを避けるため DO NOTHING で初回のみ投入する。
-- 既存行が運用者によりカスタマイズされている場合は温存される（CLAUDE.md 原則2）。
INSERT INTO business_type (code, display_name, short_name) VALUES
    ('01', 'しまむら',   'sm'),
    ('02', 'アベイル',   'av'),
    ('03', '思夢樂',     'sr'),
    ('04', 'バースデイ', 'br'),
    ('05', 'シャンブル', 'cm'),
    ('06', 'ディバロ',   'di')
ON CONFLICT (code) DO NOTHING;

-- ------------------------------------------------------------
-- 商品マスタ（運用側で手動投入される参照データ）
-- ------------------------------------------------------------
--  m_product   : 商品の親（業態 × 商品記号 × 品番で1行）
--  m_product_sku: SKU（カラー × サイズ × 単品コード）。画像は SKU + image_index で多枚保持。
--
--  sales_weekly との結合キー対応:
--    sales_weekly.gyotai_code   = m_product.business_category_cd
--    sales_weekly.shohin_kigou  = m_product.product_sign
--    sales_weekly.hinban_code   = m_product.product_type_crd
--    sales_weekly.tanpin_code   = m_product_sku.unit_cd
--
--  SoTは投入元の運用ファイル（手動投入）。アプリは表示・分析のみ行う。
-- ------------------------------------------------------------
CREATE TABLE IF NOT EXISTS m_product (
    product_id              uuid          NOT NULL,
    business_category_cd    varchar(2)    NOT NULL,
    business_category_sign  varchar(50)   NOT NULL,
    division_cd             integer       NOT NULL,
    division_name           varchar(50)   NOT NULL,
    product_name            varchar(255)  NOT NULL,
    brand                   varchar(100),
    product_sign            varchar(50)   NOT NULL,
    manager                 varchar(100),
    product_type_crd        varchar(50)   NOT NULL,
    created_at              timestamptz   NOT NULL DEFAULT now(),
    updated_at              timestamptz   NOT NULL DEFAULT now(),

    CONSTRAINT pk_m_product PRIMARY KEY (product_id)
);

COMMENT ON TABLE  m_product IS '商品マスタ（親）。業態×商品記号×品番で一意';
COMMENT ON COLUMN m_product.business_category_cd  IS '業態コード（sales_weekly.gyotai_code と対応）';
COMMENT ON COLUMN m_product.business_category_sign IS '業態の表示用記号';
COMMENT ON COLUMN m_product.division_cd           IS '部門コード（数値。例 11, 12, 51, 56）';
COMMENT ON COLUMN m_product.division_name         IS '部門表示名';
COMMENT ON COLUMN m_product.product_name          IS '商品名';
COMMENT ON COLUMN m_product.product_sign          IS '商品記号（sales_weekly.shohin_kigou と対応）';
COMMENT ON COLUMN m_product.product_type_crd      IS '品番コード（sales_weekly.hinban_code と対応）';
COMMENT ON COLUMN m_product.brand                 IS 'ブランド名（任意）';
COMMENT ON COLUMN m_product.manager               IS '担当者名（任意）';

-- 業務上の自然キー。手動投入時の重複防止と sales_weekly との結合インデックス。
-- 既存環境で運用者が UNIQUE 制約のないテーブルへ既にデータ投入済みの場合、後から
-- UNIQUE INDEX を作成すると重複違反 (23505) で失敗するため、エラーを捕捉してスキップする。
-- 新規環境では通常通り UNIQUE INDEX が作成される。スキップされた場合は運用者が
-- データ整合（重複行の整理）後に手動で `CREATE UNIQUE INDEX` を実行することを想定する。
DO $$
BEGIN
    CREATE UNIQUE INDEX IF NOT EXISTS ux_m_product_business_key
        ON m_product (business_category_cd, product_sign, product_type_crd);
EXCEPTION WHEN unique_violation THEN
    RAISE NOTICE 'ux_m_product_business_key の作成をスキップしました（既存データに重複あり）。データ整合後に手動で UNIQUE INDEX を作成してください。';
END
$$;

CREATE INDEX IF NOT EXISTS ix_m_product_division_cd ON m_product (division_cd);
CREATE INDEX IF NOT EXISTS ix_m_product_brand       ON m_product (brand);
CREATE INDEX IF NOT EXISTS ix_m_product_manager     ON m_product (manager);

CREATE TABLE IF NOT EXISTS m_product_sku (
    sku_item_id     uuid          NOT NULL,
    product_id      uuid          NOT NULL,
    unit_cd         varchar(50)   NOT NULL,
    color_name      varchar(50)   NOT NULL,
    size_name       varchar(50)   NOT NULL,
    sales_price     integer       NOT NULL DEFAULT 0,
    cost_price      integer       NOT NULL DEFAULT 0,
    image_id        uuid          NOT NULL,
    image_index     integer       NOT NULL,
    image_file_name varchar(255),
    image_url       text          NOT NULL,
    created_at      timestamptz   NOT NULL DEFAULT now(),
    updated_at      timestamptz   NOT NULL DEFAULT now(),

    CONSTRAINT pk_m_product_sku PRIMARY KEY (sku_item_id),
    CONSTRAINT fk_m_product_sku_parent FOREIGN KEY (product_id)
        REFERENCES m_product (product_id) ON DELETE CASCADE
);

COMMENT ON TABLE  m_product_sku IS '商品SKU。1行=SKU×画像（単品×色×サイズ×画像index）';
COMMENT ON COLUMN m_product_sku.unit_cd     IS '単品コード（sales_weekly.tanpin_code と対応）';
COMMENT ON COLUMN m_product_sku.image_index IS '同一SKU内での画像表示順（0=サムネ既定）';
COMMENT ON COLUMN m_product_sku.image_url   IS '画像配信URL（外部CDN想定）';

-- SKU 単位（単品×色×サイズ）のクエリと、sales_weekly との結合性能を確保する。
CREATE INDEX IF NOT EXISTS ix_m_product_sku_product
    ON m_product_sku (product_id);
CREATE INDEX IF NOT EXISTS ix_m_product_sku_product_unit
    ON m_product_sku (product_id, unit_cd);
CREATE INDEX IF NOT EXISTS ix_m_product_sku_unit_cd
    ON m_product_sku (unit_cd);

-- 日次粒度の集計は、週次ファクトを取込日で先に集計してから日次7列を展開する
-- 方式（アプリ側クエリ）で行う。160万行を縦展開する前に集約するため高速。
-- 日付対応ロジック: sales_date = import_date - 8 + day_index
--   day_index 1 → import_date-7（月） / day_index 7 → import_date-1（日）

-- ============================================================
--  分析 mart（スタースキーマ） — docs/star-schema-design.md
-- ------------------------------------------------------------
--  既存 sales_weekly（取込ソース層）を温存しつつ、分析用に
--  ディメンショナルモデルを別スキーマ mart に構築する。
--  本スキーマは派生（キャッシュ）であり、SoT は sales_weekly。
--  mart.rebuild() で sales_weekly + 商品マスタから全再構築する。
--
--  範囲:
--   * 次元: dim_date / dim_retailer / dim_product / dim_sku（全て SCD1）／ dim_climate（気温日次・CSV由来）
--   * ファクト: fact_sales_weekly（週次フロー）／ fact_inventory_snapshot（在庫スナップショット）
--     （グレインはいずれも 週×小売×SKU）
--   * 日次派生ファクト・テナント別スキーマ分離・集約マテビューは後続。
-- ============================================================
CREATE SCHEMA IF NOT EXISTS mart;

-- 日付次元（週=取込日の月曜）
CREATE TABLE IF NOT EXISTS mart.dim_date (
    date_key    integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    week_monday date    NOT NULL UNIQUE,
    iso_year    integer NOT NULL,
    iso_week    integer NOT NULL,
    year        integer NOT NULL,
    quarter     integer NOT NULL,
    month       integer NOT NULL
);
COMMENT ON TABLE mart.dim_date IS '日付次元（週=月曜）。sales_weekly.import_date から導出';

-- 小売次元（企業集約。channel=業態）
CREATE TABLE IF NOT EXISTS mart.dim_retailer (
    retailer_key  integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    retailer_code text NOT NULL,
    retailer_name text,
    channel_code  text NOT NULL,
    channel_name  text,
    UNIQUE (retailer_code, channel_code)
);
COMMENT ON TABLE mart.dim_retailer IS '小売次元（企業集約）。retailer=customer_code / channel=業態(gyotai_code)';

-- 商品次元（SCD1。自然キー=業態×記号×品番）
CREATE TABLE IF NOT EXISTS mart.dim_product (
    product_key     integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    channel_code    text NOT NULL,
    product_sign    text NOT NULL,
    product_code    text NOT NULL,
    product_name    text,
    department_code text,
    department_name text,
    brand           text,
    manager         text,
    category        text,
    season          text,
    attributes      jsonb NOT NULL DEFAULT '{}'::jsonb,
    UNIQUE (channel_code, product_sign, product_code)
);
COMMENT ON TABLE mart.dim_product IS '商品次元（SCD1）。業種固有属性（季節・棚割）は attributes(jsonb)';

-- 業種固有属性のうち頻用する季節は生成列＋索引で集計性能を担保（設計 §5.2）
ALTER TABLE mart.dim_product
    ADD COLUMN IF NOT EXISTS season_attr text
    GENERATED ALWAYS AS (attributes->>'season') STORED;
CREATE INDEX IF NOT EXISTS ix_dim_product_department ON mart.dim_product (department_code);
CREATE INDEX IF NOT EXISTS ix_dim_product_season     ON mart.dim_product (season);

-- SKU次元（SCD1。汎用バリアント2軸＋定価）
CREATE TABLE IF NOT EXISTS mart.dim_sku (
    sku_key             integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    product_key         integer NOT NULL REFERENCES mart.dim_product(product_key),
    unit_code           text NOT NULL,
    variant_axis1_label text,
    variant_axis1_value text,
    variant_axis2_label text,
    variant_axis2_value text,
    list_price          integer,
    image_url           text,
    UNIQUE (product_key, unit_code)
);
COMMENT ON TABLE mart.dim_sku IS 'SKU次元（SCD1）。色/サイズは汎用バリアント2軸。list_price=定価（現在値）';

-- 業種固有属性（設計 §4.5）。導入日 donyu は YYYYMMDD 文字列のまま保持する
-- （ソース sales_weekly.donyu_date は text。"0"=未設定。文字列比較は日付順と一致するため
--  範囲フィルタに使え、不正値で再構築（to_date）が失敗するリスクを避ける）。
ALTER TABLE mart.dim_sku
    ADD COLUMN IF NOT EXISTS attributes jsonb NOT NULL DEFAULT '{}'::jsonb;

-- 売上ファクト（週次フロー。グレイン=週×SKU×小売。数量/金額/粗利は事前計算）
CREATE TABLE IF NOT EXISTS mart.fact_sales_weekly (
    date_key     integer NOT NULL REFERENCES mart.dim_date(date_key),
    retailer_key integer NOT NULL REFERENCES mart.dim_retailer(retailer_key),
    product_key  integer NOT NULL REFERENCES mart.dim_product(product_key),
    sku_key      integer NOT NULL REFERENCES mart.dim_sku(sku_key),
    quantity     bigint  NOT NULL,
    amount       bigint  NOT NULL,
    gross_profit bigint  NOT NULL,
    sale_price   integer NOT NULL,
    cost_price   integer NOT NULL,
    PRIMARY KEY (date_key, retailer_key, product_key, sku_key)
);
COMMENT ON TABLE mart.fact_sales_weekly IS '売上ファクト（週次フロー）。amount=数量×売価, gross_profit=数量×(売価−原価) を事前計算';
CREATE INDEX IF NOT EXISTS ix_fact_sw_date     ON mart.fact_sales_weekly (date_key);
CREATE INDEX IF NOT EXISTS ix_fact_sw_product  ON mart.fact_sales_weekly (product_key);
CREATE INDEX IF NOT EXISTS ix_fact_sw_retailer ON mart.fact_sales_weekly (retailer_key);

-- mart のメタ情報（最終再構築時刻）。1行のみ。
CREATE TABLE IF NOT EXISTS mart.build_info (
    id           integer PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    rebuilt_at   timestamptz,
    source_rows  bigint NOT NULL DEFAULT 0,
    fact_rows    bigint NOT NULL DEFAULT 0
);
INSERT INTO mart.build_info (id) VALUES (1) ON CONFLICT (id) DO NOTHING;

-- 非同期再構築の進捗管理列（冪等に追加）。
-- status はアプリ側で idle → running → completed / failed と遷移させる。
ALTER TABLE mart.build_info
    ADD COLUMN IF NOT EXISTS status     text NOT NULL DEFAULT 'idle',
    ADD COLUMN IF NOT EXISTS error      text,
    ADD COLUMN IF NOT EXISTS started_at timestamptz;
DO $$
BEGIN
    ALTER TABLE mart.build_info
        ADD CONSTRAINT mart_build_info_status_chk
        CHECK (status IN ('idle', 'running', 'completed', 'failed'));
EXCEPTION WHEN duplicate_object THEN NULL;
END
$$;

-- 在庫スナップショットファクト（ピリオディック・スナップショット。グレイン=週×小売×SKU）
--  設計 §3.3。在庫・累計は時点値（時間方向に非加算／SKU・小売方向に加算可）、在日は平均で集計。
--  SKU は単一商品に属する（dim_sku.product_key）ため product_key も保持し、部門等の絞り込みを
--  dim_product 経由で fact_sales_weekly と対称に行えるようにする（グレインは週×小売×SKU のまま）。
--  これにより約20箇所に散在する「期間内最新 import_date で在庫取得」ロジックが本テーブル参照に一元化される。
CREATE TABLE IF NOT EXISTS mart.fact_inventory_snapshot (
    date_key     integer NOT NULL REFERENCES mart.dim_date(date_key),
    retailer_key integer NOT NULL REFERENCES mart.dim_retailer(retailer_key),
    product_key  integer NOT NULL REFERENCES mart.dim_product(product_key),
    sku_key      integer NOT NULL REFERENCES mart.dim_sku(sku_key),
    stock        bigint           NOT NULL,
    cum_sales    bigint           NOT NULL,
    cum_delivery bigint           NOT NULL,
    order_qty    numeric(14,1)    NOT NULL,
    advance_qty  bigint           NOT NULL,
    stock_days   double precision NOT NULL,
    PRIMARY KEY (date_key, retailer_key, product_key, sku_key)
);
COMMENT ON TABLE mart.fact_inventory_snapshot IS
    '在庫スナップショット（週次・セミ加算）。stock/cum_sales/cum_delivery は時点値、stock_days は在日（平均集計）';
CREATE INDEX IF NOT EXISTS ix_fact_inv_date     ON mart.fact_inventory_snapshot (date_key);
CREATE INDEX IF NOT EXISTS ix_fact_inv_product  ON mart.fact_inventory_snapshot (product_key);
CREATE INDEX IF NOT EXISTS ix_fact_inv_retailer ON mart.fact_inventory_snapshot (retailer_key);

-- 気温次元（日次・エリア別）。db/climate_daily.csv を DataLoader が投入する（sales 非依存）。
--  area_code: standard=東京 / cold=札幌 / warm=那覇（ClimateModel のエリア種別と一致）。
--  売上週への結合は the_date の範囲結合（FK は張らず疎結合に保つ）。mart.rebuild() では触らない
--  （sales_weekly 由来ではないため、再構築の TRUNCATE/CASCADE 対象に含めない）。
CREATE TABLE IF NOT EXISTS mart.dim_climate (
    area_code text NOT NULL,
    the_date  date NOT NULL,
    temp_avg  double precision NOT NULL,
    temp_max  double precision NOT NULL,
    temp_min  double precision NOT NULL,
    PRIMARY KEY (area_code, the_date)
);
COMMENT ON TABLE mart.dim_climate IS
    '気温次元（日次・エリア別）。出典 db/climate_daily.csv。standard=東京/cold=札幌/warm=那覇';
CREATE INDEX IF NOT EXISTS ix_dim_climate_date ON mart.dim_climate (the_date);

-- ------------------------------------------------------------
--  mart 全再構築（sales_weekly + 商品マスタ → 次元・ファクト）
--  冪等: 何度呼んでも同じ結果。advisory lock で再構築を直列化する。
--  代表値（名称・季節・色/サイズ・定価）は各自然キーの最新取込週を採用。
-- ------------------------------------------------------------
CREATE OR REPLACE FUNCTION mart.rebuild() RETURNS void
LANGUAGE plpgsql AS $$
DECLARE
    v_source_rows bigint;
    v_fact_rows   bigint;
BEGIN
    -- 再構築は有界だが長時間（数分〜十数分）に及ぶ。サーバ側に statement_timeout が
    -- 設定されていても、本トランザクション内では無効化して中断されないようにする
    -- （クライアント側の CommandTimeout=0 と合わせ、再構築がタイムアウトしないことを保証する）。
    SET LOCAL statement_timeout = 0;

    -- 'MART'(0x4D415254) を鍵に再構築を直列化する。
    PERFORM pg_advisory_xact_lock(1296913492);

    -- dim_climate は sales 非依存（CSV 由来）のため TRUNCATE 対象に含めない。
    -- dim_* への CASCADE は両ファクト（売上・在庫）に波及するが、明示して意図を残す。
    TRUNCATE mart.fact_sales_weekly, mart.fact_inventory_snapshot,
             mart.dim_sku, mart.dim_product,
             mart.dim_retailer, mart.dim_date RESTART IDENTITY CASCADE;

    -- 日付次元
    INSERT INTO mart.dim_date (week_monday, iso_year, iso_week, year, quarter, month)
    SELECT DISTINCT
           sw.import_date,
           EXTRACT(ISOYEAR  FROM sw.import_date)::int,
           EXTRACT(WEEK     FROM sw.import_date)::int,
           EXTRACT(YEAR     FROM sw.import_date)::int,
           EXTRACT(QUARTER  FROM sw.import_date)::int,
           EXTRACT(MONTH    FROM sw.import_date)::int
    FROM sales_weekly sw;

    -- 小売次元（業態名は business_type マスタから）
    INSERT INTO mart.dim_retailer (retailer_code, retailer_name, channel_code, channel_name)
    SELECT DISTINCT sw.customer_code, NULL::text, sw.gyotai_code, bt.display_name
    FROM sales_weekly sw
    LEFT JOIN business_type bt ON bt.code = sw.gyotai_code;

    -- 商品次元（自然キーの最新取込週を代表値に）。
    -- 商品マスタ m_product は自然キー（業態×記号×品番）に重複があり得る
    -- （重複により ux_m_product_business_key が未作成の運用環境）。LATERAL + LIMIT 1 で
    -- 1件（updated_at 最新）に絞り、LEFT JOIN の行増幅と dim_product の一意制約違反を防ぐ。
    INSERT INTO mart.dim_product
        (channel_code, product_sign, product_code, product_name,
         department_code, department_name, brand, manager, category, season, attributes)
    SELECT s.gyotai_code, s.shohin_kigou, s.hinban_code,
           COALESCE(mp.product_name, s.hinmei),
           s.department, mp.division_name, mp.brand, mp.manager,
           NULL::text, NULLIF(s.kisetsu, ''),
           jsonb_strip_nulls(jsonb_build_object(
               'season',    NULLIF(s.kisetsu, ''),
               'tanawari1', NULLIF(s.tanawari1, ''),
               'tanawari2', NULLIF(s.tanawari2, '')))
    FROM (
        SELECT DISTINCT ON (gyotai_code, shohin_kigou, hinban_code)
               gyotai_code, shohin_kigou, hinban_code,
               hinmei, department, kisetsu, tanawari1, tanawari2
        FROM sales_weekly
        -- 同一キー・同一週の複数行に備え id DESC まで指定し、代表行を決定的にする（冪等性）。
        ORDER BY gyotai_code, shohin_kigou, hinban_code, import_date DESC, id DESC
    ) s
    LEFT JOIN (
        -- 商品マスタを自然キーで1回だけ重複排除（updated_at 最新を代表）。
        -- LATERAL（行数ぶんの再スキャン）を避け、インデックス未整備でも高速に結合する。
        SELECT DISTINCT ON (business_category_cd, product_sign, product_type_crd)
               business_category_cd, product_sign, product_type_crd,
               product_name, division_name, brand, manager
        FROM m_product
        ORDER BY business_category_cd, product_sign, product_type_crd,
                 updated_at DESC, product_id
    ) mp
        ON mp.business_category_cd = s.gyotai_code
       AND mp.product_sign        = s.shohin_kigou
       AND mp.product_type_crd    = s.hinban_code;

    -- SKU次元（定価・代表画像は商品マスタから）。
    -- 導入日 donyu は最新取込行の値（SCD1）。同一 SKU・同一週に複数導入日の行があり得る
    -- （sales_weekly の一意キーに donyu_date を含む）ため、donyu_date DESC → id DESC の
    -- 決定的な tie-break で代表行を固定する（冪等性: 再実行で同じ結果）。
    INSERT INTO mart.dim_sku
        (product_key, unit_code, variant_axis1_label, variant_axis1_value,
         variant_axis2_label, variant_axis2_value, list_price, image_url, attributes)
    SELECT dp.product_key, s.tanpin_code,
           'カラー', NULLIF(s.color, ''),
           'サイズ', NULLIF(s.size, ''),
           sk.sales_price, sk.image_url,
           jsonb_strip_nulls(jsonb_build_object(
               -- '0'（未設定）のほか '00000000' も非日付のため除外する
               -- （商品単位の導入日は MIN(donyu) で集計するため、最小に張り付く偽値を入れない）。
               'donyu', CASE WHEN s.donyu_date ~ '^[0-9]{8}$' AND s.donyu_date <> '00000000'
                             THEN s.donyu_date END))
    FROM (
        SELECT DISTINCT ON (gyotai_code, shohin_kigou, hinban_code, tanpin_code)
               gyotai_code, shohin_kigou, hinban_code, tanpin_code, color, size, donyu_date
        FROM sales_weekly
        ORDER BY gyotai_code, shohin_kigou, hinban_code, tanpin_code,
                 import_date DESC, donyu_date DESC, id DESC
    ) s
    JOIN mart.dim_product dp
        ON dp.channel_code = s.gyotai_code
       AND dp.product_sign = s.shohin_kigou
       AND dp.product_code = s.hinban_code
    LEFT JOIN (
        -- SKU（業態×記号×品番×単品）ごとに代表（画像 index 最小）を1回で確定する。
        -- LATERAL の再スキャンを避け、インデックス未整備でも高速に結合する。
        SELECT DISTINCT ON (mp.business_category_cd, mp.product_sign, mp.product_type_crd, msk.unit_cd)
               mp.business_category_cd, mp.product_sign, mp.product_type_crd, msk.unit_cd,
               msk.sales_price, msk.image_url
        FROM m_product mp
        JOIN m_product_sku msk ON msk.product_id = mp.product_id
        ORDER BY mp.business_category_cd, mp.product_sign, mp.product_type_crd, msk.unit_cd,
                 msk.image_index, msk.sku_item_id
    ) sk
        ON sk.business_category_cd = s.gyotai_code
       AND sk.product_sign        = s.shohin_kigou
       AND sk.product_type_crd    = s.hinban_code
       AND sk.unit_cd             = s.tanpin_code;

    -- 売上ファクト＋在庫スナップショットを「1回の集約」から両方構築する。
    -- 両ファクトはグレイン（週×小売×SKU）と次元結合が完全に同一で、測定値だけが異なる。
    -- そこで sales_weekly の走査・次元結合・GROUP BY を1度だけ行い（agg）、データ変更CTEで
    -- 両ファクトへ流し込む（agg は複数参照のため1回だけマテリアライズされる）。
    -- これにより 160万行の二重走査・二重結合を避け、再構築時間を約半減する
    -- （在庫追加で従来比2倍となり command timeout を超えた問題への対処）。
    WITH agg AS (
        SELECT dd.date_key, dr.retailer_key, dp.product_key, ds.sku_key,
               SUM(q.qty)::bigint                         AS quantity,
               SUM(q.qty * sw.baika)::bigint              AS amount,
               SUM(q.qty * (sw.baika - sw.genka))::bigint AS gross_profit,
               MAX(sw.baika)                              AS sale_price,
               MAX(sw.genka)                              AS cost_price,
               SUM(sw.zaikosu)::bigint                    AS stock,
               SUM(sw.ruikei_uriage_count)::bigint        AS cum_sales,
               SUM(sw.ruikei_nohin_count)::bigint         AS cum_delivery,
               SUM(sw.hatchu_count)                       AS order_qty,
               SUM(sw.sakizuke_count)::bigint             AS advance_qty,
               COALESCE(AVG(sw.zainiti), 0)::float8       AS stock_days
        FROM sales_weekly sw
        CROSS JOIN LATERAL (SELECT (sw.toshu_uriage_count1 + sw.toshu_uriage_count2
               + sw.toshu_uriage_count3 + sw.toshu_uriage_count4 + sw.toshu_uriage_count5
               + sw.toshu_uriage_count6 + sw.toshu_uriage_count7) AS qty) q
        JOIN mart.dim_date     dd ON dd.week_monday   = sw.import_date
        JOIN mart.dim_retailer dr ON dr.retailer_code = sw.customer_code
                                 AND dr.channel_code  = sw.gyotai_code
        JOIN mart.dim_product  dp ON dp.channel_code  = sw.gyotai_code
                                 AND dp.product_sign  = sw.shohin_kigou
                                 AND dp.product_code  = sw.hinban_code
        JOIN mart.dim_sku      ds ON ds.product_key   = dp.product_key
                                 AND ds.unit_code     = sw.tanpin_code
        GROUP BY dd.date_key, dr.retailer_key, dp.product_key, ds.sku_key
    ),
    ins_sales AS (
        INSERT INTO mart.fact_sales_weekly
            (date_key, retailer_key, product_key, sku_key,
             quantity, amount, gross_profit, sale_price, cost_price)
        SELECT date_key, retailer_key, product_key, sku_key,
               quantity, amount, gross_profit, sale_price, cost_price
        FROM agg
    )
    INSERT INTO mart.fact_inventory_snapshot
        (date_key, retailer_key, product_key, sku_key,
         stock, cum_sales, cum_delivery, order_qty, advance_qty, stock_days)
    SELECT date_key, retailer_key, product_key, sku_key,
           stock, cum_sales, cum_delivery, order_qty, advance_qty, stock_days
    FROM agg;

    SELECT count(*) INTO v_source_rows FROM sales_weekly;
    SELECT count(*) INTO v_fact_rows   FROM mart.fact_sales_weekly;

    UPDATE mart.build_info
    SET rebuilt_at  = now(),
        source_rows = v_source_rows,
        fact_rows   = v_fact_rows
    WHERE id = 1;
END;
$$;
COMMENT ON FUNCTION mart.rebuild() IS 'sales_weekly + 商品マスタから mart を全再構築する（冪等・直列化）';

-- ------------------------------------------------------------
-- 在庫アクションフラグ（ユーザー操作の永続データ。記録系）
-- ------------------------------------------------------------
-- SoT は本テーブル（発注停止候補・値下げ候補というユーザーの判断記録）。
-- mart は派生キャッシュであり mart.rebuild() の TRUNCATE 対象（mart スキーマ）に
-- 含まれないため、再構築の影響を受けない。明細（mart 側）との結合は自然キー
-- （業態×商品記号×品番×単品）で行う（docs/design.md §7.x）。
-- 語彙（flag_type / status の値）の実装 SoT は backend Core の InventoryFlagRules。
CREATE TABLE IF NOT EXISTS inventory_action_flag (
    id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    gyotai_code    text NOT NULL,
    shohin_kigou   text NOT NULL,
    hinban_code    text NOT NULL,
    tanpin_code    text NOT NULL,
    flag_type      text NOT NULL CHECK (flag_type IN ('stop-order', 'markdown')),
    status         text NOT NULL DEFAULT 'candidate'
                       CHECK (status IN ('candidate', 'in-progress', 'done', 'dismissed')),
    note           text,
    -- 付与時のアンカー週（効果測定の起点）と付与時の判定状態
    -- （閾値変更後に「現在は判定対象外」を表示で検知するための根拠）。
    flagged_week   date NOT NULL,
    flagged_status text NOT NULL,
    created_by     text NOT NULL,
    created_at     timestamptz NOT NULL DEFAULT now(),
    updated_by     text NOT NULL,
    updated_at     timestamptz NOT NULL DEFAULT now()
);

-- 1 SKU × フラグ種別で一意（stop-order と markdown の併存は可）。
-- 一括登録（ON CONFLICT DO NOTHING）の冪等性と、明細結合（高々1行）の土台。
CREATE UNIQUE INDEX IF NOT EXISTS ux_inventory_action_flag_key
    ON inventory_action_flag (gyotai_code, shohin_kigou, hinban_code, tanpin_code, flag_type);

-- ============================================================
--  しまむらグループ 組織マスタ（業態 × 商品部（部署） × 部門の相関）
-- ------------------------------------------------------------
--  SoT はしまむら提供資料「お取引の基準：総括編」（掲載の部門部連絡先表）。
--  初期データは同資料 250317 版（部門部連絡先表ページの改定日 2025.3.10）から
--  投入し、以後は運用者がマスタメンテ画面
--  （/org-master）で人的に修正する（修正後はアプリ側マスタが正）。
--  業態マスタは既存 business_type を再利用する（原則3。コード 01-06 は
--  上記でシード済み）。
--  初期投入は「テーブルが空のときのみ」実行する（空テーブルガード）。
--  ON CONFLICT DO NOTHING だけでは運用者による行削除・キー列（部署名/部門CD/
--  相談内容）変更が schema.sql 再適用（デプロイ毎に DataLoader が実行）で
--  巻き戻る（削除行の復活・旧行の再出現）ため、1行でも存在すれば再投入しない
--  （原則2）。全行削除からの初期値復元は意図した回復パスとして機能する。
-- ============================================================

-- 商品部（部署）マスタ。業態ごとの「商品1部（婦人）」等と連絡先・フロア。
CREATE TABLE IF NOT EXISTS m_buyer_section (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    business_type_code text NOT NULL REFERENCES business_type (code),
    name               text NOT NULL,
    category_note      text,
    phone              text,
    floor              text,
    display_order      integer NOT NULL DEFAULT 0,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    UNIQUE (business_type_code, name),
    -- 部門側の複合FK（業態越境参照のDBレベル禁止）の参照先
    CONSTRAINT uq_buyer_section_btc_id UNIQUE (business_type_code, id)
);
COMMENT ON TABLE  m_buyer_section IS '商品部（部署）マスタ。業態ごとのバイヤー部署と連絡先（SoT: お取引の基準 総括編＋運用者修正）';
COMMENT ON COLUMN m_buyer_section.name          IS '部署名（商品1部・事業部 等）';
COMMENT ON COLUMN m_buyer_section.category_note IS '部署の取扱区分（婦人・紳士・メンズ 等）';
COMMENT ON COLUMN m_buyer_section.floor         IS '本社フロア（2F/3F/4F）';

-- 部門マスタ（業態×部門の相関）。部門コードは業態内で一意（業態が異なれば
-- 同一コードでも別部門。例: しまむら 5A=カットソー / アベイル 5A=メンズ シューズ）。
CREATE TABLE IF NOT EXISTS m_section_department (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    business_type_code text NOT NULL REFERENCES business_type (code),
    dept_code          text NOT NULL,
    dept_name          text NOT NULL,
    buyer_section_id   bigint,
    display_order      integer NOT NULL DEFAULT 0,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    UNIQUE (business_type_code, dept_code),
    -- 所属商品部は同一業態のみ（複合FK。アプリ側検証の同時編集すり抜けもDBで遮断）。
    -- 部署削除時は所属のみ NULL 化し部門は温存（PG15+ の列指定 SET NULL）。
    CONSTRAINT fk_section_department_section
        FOREIGN KEY (business_type_code, buyer_section_id)
        REFERENCES m_buyer_section (business_type_code, id)
        ON DELETE SET NULL (buyer_section_id)
);
COMMENT ON TABLE  m_section_department IS '部門マスタ（業態×部門の相関）。買付部門コードと名称・所属部署（SoT: お取引の基準 総括編＋運用者修正）';
COMMENT ON COLUMN m_section_department.dept_code        IS '部門コード（5A 等。業態内で一意）';
COMMENT ON COLUMN m_section_department.buyer_section_id IS '所属する商品部（部署）。部署削除時は NULL（部門は温存）';

-- 既存DB移行: 旧・単一列FK（buyer_section_id → m_buyer_section.id）を、業態越境参照を
-- DBレベルで禁止する複合FKへ置き換える（冪等。新規作成DBでは CREATE TABLE 内で定義済み）。
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'uq_buyer_section_btc_id') THEN
        ALTER TABLE m_buyer_section
            ADD CONSTRAINT uq_buyer_section_btc_id UNIQUE (business_type_code, id);
    END IF;
    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'm_section_department_buyer_section_id_fkey') THEN
        ALTER TABLE m_section_department DROP CONSTRAINT m_section_department_buyer_section_id_fkey;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_section_department_section') THEN
        -- 旧構成下の同時編集等で越境参照が残っていた場合は、FK 付与前に所属のみ解除する
        -- （ON DELETE SET NULL と同じ「所属解除・部門温存」の意味論。既存行検証での
        --  移行失敗＝デプロイ停止を防ぐ。制約付与済みの環境では実行されない）。
        UPDATE m_section_department d
        SET buyer_section_id = NULL
        WHERE d.buyer_section_id IS NOT NULL
          AND NOT EXISTS (
              SELECT 1 FROM m_buyer_section s
              WHERE s.id = d.buyer_section_id
                AND s.business_type_code = d.business_type_code);
        ALTER TABLE m_section_department
            ADD CONSTRAINT fk_section_department_section
            FOREIGN KEY (business_type_code, buyer_section_id)
            REFERENCES m_buyer_section (business_type_code, id)
            ON DELETE SET NULL (buyer_section_id);
    END IF;
END $$;

-- 相談受付デスクマスタ（お取引についての相談受付）。業務チャットの部署絞込にも使用。
CREATE TABLE IF NOT EXISTS m_contact_desk (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    topic           text NOT NULL,
    department_name text NOT NULL,
    phone           text,
    chat_domain     text CHECK (chat_domain IN ('system', 'quality', 'logistics')),
    display_order   integer NOT NULL DEFAULT 0,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    UNIQUE (topic)
);
COMMENT ON TABLE  m_contact_desk IS '相談受付デスクマスタ（お取引についての相談受付）。SoT: お取引の基準 総括編＋運用者修正';
COMMENT ON COLUMN m_contact_desk.chat_domain IS '業務チャットの部署キー（system=システム部/quality=商品管理部/logistics=物流部。NULL=チャット対象外）';

-- 初期データ: 商品部（部署）。総括編 250317 版の部門部連絡先表（ページ改定日 2025.3.10）より。
-- 空テーブル時のみ投入（運用者の削除・キー変更を再適用で巻き戻さない）。
INSERT INTO m_buyer_section (business_type_code, name, category_note, phone, floor, display_order)
SELECT v.*
FROM (VALUES
    ('01', '商品1部', '婦人',                     '048-631-2151', '2F', 1),
    ('01', '商品2部', '婦人',                     '048-631-2152', '2F', 2),
    ('01', '商品3部', '紳士',                     '048-631-2153', '2F', 3),
    ('01', '商品4部', NULL,                       '048-631-2154', '2F', 4),
    ('01', '商品5部', NULL,                       '048-631-2155', '3F', 5),
    ('01', '商品6部', NULL,                       '048-631-2156', '3F', 6),
    ('01', '商品7部', NULL,                       '048-631-2157', '3F', 7),
    ('02', '商品1部', 'メンズ',                   '048-631-2161', '4F', 1),
    ('02', '商品2部', 'レディース',               '048-631-2162', '4F', 2),
    ('02', '商品3部', 'レディースシューズ・服飾他', '048-631-2163', '4F', 3),
    ('02', '商品4部', 'メンズシューズ・服飾他',    '048-631-2163', '4F', 4),
    ('04', '商品1部', NULL,                       '048-631-2171', '3F', 1),
    ('04', '商品2部', NULL,                       '048-631-2172', '3F', 2),
    ('04', '商品3部', NULL,                       '048-631-2173', '3F', 3),
    ('05', '商品1部', NULL,                       '048-631-2177', '4F', 1),
    ('05', '商品2部', NULL,                       '048-631-2127', '4F', 2),
    ('06', '事業部',  NULL,                       '048-631-2179', '3F', 1)
) AS v(business_type_code, name, category_note, phone, floor, display_order)
WHERE NOT EXISTS (SELECT 1 FROM m_buyer_section)
ON CONFLICT (business_type_code, name) DO NOTHING;

-- 初期データ: 部門（業態×部門の相関）。所属部署は名前でルックアップして紐づける。
-- 空テーブル時のみ投入（運用者の削除・キー変更を再適用で巻き戻さない）。
INSERT INTO m_section_department (business_type_code, dept_code, dept_name, buyer_section_id, display_order)
SELECT v.btc, v.code, v.name, bs.id, v.ord
FROM (VALUES
    -- しまむら（01）
    ('01', '5A', 'カットソー',                         '商品1部',  1),
    ('01', '5B', '布帛・ニット',                       '商品1部',  2),
    ('01', '5D', 'ジャケット・スポーツ・フォーマル',   '商品1部',  3),
    ('01', '5E', 'ボトムス',                           '商品1部',  4),
    ('01', '5C', 'ティーンズ',                         '商品2部',  5),
    ('01', '5F', 'ラージサイズ',                       '商品2部',  6),
    ('01', '5G', 'ミセス・シルバー',                   '商品2部',  7),
    ('01', '2A', 'アダルト・シニア',                   '商品3部',  8),
    ('01', '2B', 'テイーンズ・ヤング・ラージサイズ',   '商品3部',  9),
    ('01', '2C', 'スポーツ・リラクシング・ビジネス',   '商品3部', 10),
    ('01', '2D', 'ラージサイズ・服飾雑貨',             '商品3部', 11),
    ('01', '6A', 'ベビー用品・玩具',                   '商品4部', 12),
    ('01', '7A', '男児',                               '商品4部', 13),
    ('01', '7B', '女児',                               '商品4部', 14),
    ('01', '3C', '婦人肌着',                           '商品5部', 15),
    ('01', '4A', '紳士・子供肌着',                     '商品5部', 16),
    ('01', '4B', '靴下',                               '商品5部', 17),
    ('01', '1B', '靴',                                 '商品6部', 18),
    ('01', '3A', '雑貨',                               '商品6部', 19),
    ('01', '3B', 'バッグ・服飾小物',                   '商品6部', 20),
    ('01', '1A', 'インテリア',                         '商品7部', 21),
    ('01', '8A', '寝具',                               '商品7部', 22),
    ('01', '8B', 'ナイティー',                         '商品7部', 23),
    -- アベイル（02）
    ('02', '1A', 'ヤング',                             '商品1部',  1),
    ('02', '1C', 'アダルト',                           '商品1部',  2),
    ('02', '2A', 'ティーンズ',                         '商品1部',  3),
    ('02', '2D', 'ラージサイズ',                       '商品1部',  4),
    ('02', '3A', 'ティーンズ',                         '商品2部',  5),
    ('02', '3B', 'ノンエイジ',                         '商品2部',  6),
    ('02', '4A', 'ヤング',                             '商品2部',  7),
    ('02', '4C', 'アダルト',                           '商品2部',  8),
    ('02', '4D', 'ラージサイズ',                       '商品2部',  9),
    ('02', '5B', 'レディース シューズ',                '商品3部', 10),
    ('02', '6B', 'レディース 服飾雑貨',                '商品3部', 11),
    ('02', '6C', 'インテリア・寝具・生活雑貨',         '商品3部', 12),
    ('02', '7B', 'レディース アンダーウエアー・ソックス', '商品3部', 13),
    ('02', '5A', 'メンズ シューズ',                    '商品4部', 14),
    ('02', '6A', 'メンズ 服飾雑貨',                    '商品4部', 15),
    ('02', '7A', 'メンズアンダーウエアー・ソックス',   '商品4部', 16),
    -- バースデイ（04）
    ('04', '1A', 'ベビーアウター',                     '商品1部',  1),
    ('04', '2A', 'トドラーアウター',                   '商品1部',  2),
    ('04', '2B', 'ジュニアアウター',                   '商品1部',  3),
    ('04', '3A', '服飾',                               '商品1部',  4),
    ('04', '3B', '学用品雑貨',                         '商品2部',  5),
    ('04', '5A', '新生児用品・ベビーインナー',         '商品2部',  6),
    ('04', '5B', 'キッズインナー',                     '商品2部',  7),
    ('04', '4A', 'マタニティ・大物育児用品',           '商品3部',  8),
    ('04', '6A', '玩具',                               '商品3部',  9),
    ('04', '7A', '家具・寝具',                         '商品3部', 10),
    ('04', '8A', 'フード・衛生雑貨',                   '商品3部', 11),
    -- シャンブル（05）
    ('05', '5A', '婦人アウター・靴下',                 '商品1部',  1),
    ('05', '6A', 'アクセサリー・服飾小物',             '商品1部',  2),
    ('05', '6B', '靴・バッグ',                         '商品1部',  3),
    ('05', '2A', 'インテリア・寝具',                   '商品2部',  4),
    ('05', '3A', '収納・雑貨・バス',                   '商品2部',  5),
    ('05', '4A', 'キッチン・ランチ用品',               '商品2部',  6),
    ('05', '7A', 'コスメ・文具',                       '商品2部',  7),
    -- ディバロ（06）
    ('06', '1A', 'レディースミセス',                   '事業部',   1),
    ('06', '1B', 'レディースヤング',                   '事業部',   2),
    ('06', '2A', 'メンズ',                             '事業部',   3),
    ('06', '3A', 'キッズ',                             '事業部',   4),
    ('06', '4A', '服飾雑貨',                           '事業部',   5),
    ('06', '5A', 'レディースアウター',                 '事業部',   6)
) AS v(btc, code, name, section_name, ord)
JOIN m_buyer_section bs
  ON bs.business_type_code = v.btc AND bs.name = v.section_name
WHERE NOT EXISTS (SELECT 1 FROM m_section_department)
ON CONFLICT (business_type_code, dept_code) DO NOTHING;

-- 初期データ: 相談受付デスク。空テーブル時のみ投入（運用者の削除・キー変更を再適用で巻き戻さない）。
INSERT INTO m_contact_desk (topic, department_name, phone, chat_domain, display_order)
SELECT v.*
FROM (VALUES
    ('お支払い関連',                    '経理部',     '048-631-2134', NULL,        1),
    ('Web-EDI・受発注・システム関連',   'システム2部', '048-631-2136', 'system',    2),
    ('直流・納品・物流',                '物流部',     '048-631-2144', 'logistics', 3),
    ('消耗品',                          '総務部',     '048-631-2111', NULL,        4),
    ('品質管理・品質規格',              '商品管理部', '048-631-2142', 'quality',   5),
    ('EC関連',                          'ＥＣ事業部', '048-631-2145', NULL,        6)
) AS v(topic, department_name, phone, chat_domain, display_order)
WHERE NOT EXISTS (SELECT 1 FROM m_contact_desk)
ON CONFLICT (topic) DO NOTHING;

-- ============================================================
--  副資材チェック（サプライヤーの出荷前 TAG 検品）
-- ------------------------------------------------------------
--  subsidiary_check       : AI チェックの実行記録。SoT は本テーブル（記録系データ。
--                           再実行で既存の判定・履歴を巻き戻さない・原則2）。
--  subsidiary_check_image : チェック対象の画像原本（指示書・タグ）。チェックに従属し、
--                           チェック削除で連鎖削除される。
--  m_product_attachment   : 商品マスタ付属情報。SoT は運用部門の管理ファイル→SQL投入
--                           （商品マスタ本体と同じ運用。アプリからは読み取りのみ）。
--  uuid PK はアプリ側生成（m_product と同じ流儀。Dapper INSERT 時に Guid.NewGuid()）。
-- ============================================================
CREATE TABLE IF NOT EXISTS subsidiary_check (
    check_id       uuid PRIMARY KEY,
    product_id     uuid REFERENCES m_product(product_id) ON DELETE SET NULL,
    product_label  text NOT NULL DEFAULT '',
    status         text NOT NULL DEFAULT 'processing'
                       CHECK (status IN ('processing', 'completed', 'failed')),
    judgment       text CHECK (judgment IN ('pass', 'warn', 'fail')),
    fail_count     int  NOT NULL DEFAULT 0,
    warn_count     int  NOT NULL DEFAULT 0,
    findings       jsonb,
    ai_model       text,
    error_message  text,
    created_by     text NOT NULL DEFAULT '',
    created_at     timestamptz NOT NULL DEFAULT now(),
    checked_at     timestamptz
);

COMMENT ON TABLE  subsidiary_check IS '副資材チェックの実行記録（SoT: 本テーブル。AIチェックの記録系データ。再実行での巻き戻し禁止）';
COMMENT ON COLUMN subsidiary_check.product_id    IS '対象商品（任意）。商品マスタ削除時も記録は温存する（SET NULL）';
COMMENT ON COLUMN subsidiary_check.product_label IS '表示用スナップショット（品番等）。商品マスタ削除後も履歴表示できるよう保持する';
COMMENT ON COLUMN subsidiary_check.status        IS 'チェック状態: processing / completed / failed';
COMMENT ON COLUMN subsidiary_check.judgment      IS '判定: pass=合格 / warn=要確認 / fail=要修正（completed 時のみ）';
COMMENT ON COLUMN subsidiary_check.findings      IS '指摘配列（API ワイヤ形式と同一の JSON）。※非正規化の根拠: チェック結果は親チェック単位でのみ読む不変の記録で、指摘単位の横断検索要件がない';
COMMENT ON COLUMN subsidiary_check.ai_model      IS 'チェックに使用した AI モデル ID（表示用）';
COMMENT ON COLUMN subsidiary_check.checked_at    IS 'AI チェックの完了（または失敗確定）日時';

-- AI 実行開始日時（冪等に追加）。
-- created_at（登録日時）・checked_at（完了/失敗確定日時）とは役割が異なり、再実行（rerun）の
-- クレーム UPDATE で now() に更新される。孤児判定（processing のまま滞留超過）の基準時刻でもある。
ALTER TABLE subsidiary_check
    ADD COLUMN IF NOT EXISTS started_at timestamptz;
COMMENT ON COLUMN subsidiary_check.started_at IS '最後に AI 実行を開始した日時。孤児判定（滞留超過）とクレームの基準時刻';

-- 既存行のバックフィル（冪等）。started_at 導入前の行は登録＝実行開始とみなす。
-- 2回目以降は対象行が無く 0 行更新（再適用しても値は巻き戻らない・原則2）。
UPDATE subsidiary_check SET started_at = created_at WHERE started_at IS NULL;

CREATE INDEX IF NOT EXISTS ix_subsidiary_check_created ON subsidiary_check (created_at DESC);

CREATE TABLE IF NOT EXISTS subsidiary_check_image (
    image_id      uuid PRIMARY KEY,
    check_id      uuid NOT NULL REFERENCES subsidiary_check(check_id) ON DELETE CASCADE,
    kind          text NOT NULL CHECK (kind IN ('instruction', 'tag')),
    file_name     text NOT NULL,
    content_type  text NOT NULL,
    size_bytes    bigint NOT NULL,
    data          bytea NOT NULL,
    sort_order    int NOT NULL DEFAULT 0
);

COMMENT ON TABLE  subsidiary_check_image IS '副資材チェックの画像原本。チェック（subsidiary_check）に従属し連鎖削除される';
COMMENT ON COLUMN subsidiary_check_image.kind         IS '画像種別: instruction=指示書（正） / tag=タグ（検査対象）';
COMMENT ON COLUMN subsidiary_check_image.content_type IS 'image/jpeg または image/png';
COMMENT ON COLUMN subsidiary_check_image.sort_order   IS '同一種別内の表示順（アップロード順）';

CREATE INDEX IF NOT EXISTS ix_subsidiary_check_image_check ON subsidiary_check_image (check_id);

CREATE TABLE IF NOT EXISTS m_product_attachment (
    product_id          uuid PRIMARY KEY REFERENCES m_product(product_id) ON DELETE CASCADE,
    composition         text,
    origin_country      text,
    care_labels         text,
    color_fastness_note text,
    display_order       text,
    quality_notes       text,
    updated_at          timestamptz NOT NULL DEFAULT now()
);

COMMENT ON TABLE  m_product_attachment IS '商品マスタ付属情報（SoT: 運用部門の管理ファイル→SQL投入。商品マスタ本体と同じ運用。アプリからは読み取りのみ）';
COMMENT ON COLUMN m_product_attachment.composition         IS '組成・混率（例: 綿 100%）';
COMMENT ON COLUMN m_product_attachment.origin_country      IS '原産国（例: 中国製 / MADE IN CHINA）';
COMMENT ON COLUMN m_product_attachment.care_labels         IS '洗濯絵表示・取扱い表示の内容';
COMMENT ON COLUMN m_product_attachment.color_fastness_note IS '色落ち表示（有/無・注意文言）';
COMMENT ON COLUMN m_product_attachment.display_order       IS '表示の順序（例: 品番,サイズ,混率,洗濯表示,色落ち表示等,原産国表示,製造者）';
COMMENT ON COLUMN m_product_attachment.quality_notes       IS '注意事項・デメリット表記等';

-- ============================================================
--  副資材チェック（手入力 × タグ画像の突合検証）
-- ------------------------------------------------------------
--  既存の subsidiary_check（指示書画像 × タグ画像の AI 検品）とは別系統の、
--  「手入力した表示項目」と「タグ画像から AI が読み取った内容」を項目単位で
--  突き合わせる検証機能。基本は文字列突合、洗濯表示（選択アイコン）は列・並び順の
--  特殊突合になる（比較ロジックの SoT は Core の ManualCheckComparer）。
--
--  subsidiary_manual_check       : 手入力チェックの実行記録。SoT は本テーブル（記録系。
--                                  再実行で判定・履歴を巻き戻さない・原則2）。既存 subsidiary_check と
--                                  同じ非同期実行（processing→completed/failed・孤児 rerun）方式に揃える。
--  subsidiary_manual_check_image : 検証対象のタグ画像原本。チェックに従属し連鎖削除される。
--  subsidiary_tag_pattern        : 入力フォームを構成するタグパターン（項目の組合せ・型・単複・
--                                  突合方式）。SoT はアプリ（画面から CRUD する設定系データ）。
--  洗濯表示アイコンのマスタはコード側の静的カタログ（Core の CareIconCatalog）で持ち、
--  DB テーブルは設けない（原本 SVG を含む不変の参照データのため）。
--  uuid PK はアプリ側生成（m_product / subsidiary_check と同じ流儀）。
--  テーブル定義順は FK 依存に従う（subsidiary_tag_pattern → subsidiary_manual_check →
--  subsidiary_manual_check_image。参照先を先に作成する）。
-- ============================================================
CREATE TABLE IF NOT EXISTS subsidiary_tag_pattern (
    pattern_id  uuid PRIMARY KEY,
    name        text NOT NULL,
    description text NOT NULL DEFAULT '',
    -- 項目定義の配列（key/label/valueType/multiple/compare/required。API ワイヤ形式と同一の JSON）。
    -- 項目は「フォーム構成」と「突合方式」の両方を駆動するため、両者で単一の定義を共有する（原則3・原則6）。
    fields      jsonb NOT NULL,
    is_active   boolean NOT NULL DEFAULT true,
    created_by  text NOT NULL DEFAULT '',
    created_at  timestamptz NOT NULL DEFAULT now(),
    updated_by  text NOT NULL DEFAULT '',
    updated_at  timestamptz NOT NULL DEFAULT now()
);

COMMENT ON TABLE  subsidiary_tag_pattern IS '手入力チェックのタグパターン（SoT: アプリ。画面から CRUD する設定系データ）。項目の組合せ・型・単複・突合方式を定義し、入力フォーム構成と突合方式の両方を駆動する';
COMMENT ON COLUMN subsidiary_tag_pattern.fields    IS '項目定義配列（key/label/valueType(string|number)/multiple/compare(text|ratio|careIcon)/required。API ワイヤ形式と同一の JSON）';
COMMENT ON COLUMN subsidiary_tag_pattern.is_active IS '新規チェックのパターン選択肢に出すか（false=アーカイブ。既存チェックの pattern_id 参照は温存）';

CREATE INDEX IF NOT EXISTS ix_subsidiary_tag_pattern_active
    ON subsidiary_tag_pattern (is_active, updated_at DESC);

CREATE TABLE IF NOT EXISTS subsidiary_manual_check (
    check_id       uuid PRIMARY KEY,
    product_id     uuid REFERENCES m_product(product_id) ON DELETE SET NULL,
    product_label  text NOT NULL DEFAULT '',
    pattern_id     uuid REFERENCES subsidiary_tag_pattern(pattern_id) ON DELETE SET NULL,
    pattern_name   text NOT NULL DEFAULT '',
    status         text NOT NULL DEFAULT 'processing'
                       CHECK (status IN ('processing', 'completed', 'failed')),
    judgment       text CHECK (judgment IN ('pass', 'warn', 'fail')),
    field_count    int  NOT NULL DEFAULT 0,
    match_count    int  NOT NULL DEFAULT 0,
    mismatch_count int  NOT NULL DEFAULT 0,
    warn_count     int  NOT NULL DEFAULT 0,
    -- 手入力のスナップショット（項目定義＋入力値。API ワイヤ形式と同一の JSON・camelCase）。
    -- タグパターン削除後・項目変更後も「実行時に何を入力したか」を復元できるよう非正規化保持する。
    input          jsonb NOT NULL,
    -- 項目単位の突合結果（expected/extracted/verdict 等。API ワイヤ形式と同一の JSON）。
    -- ※非正規化の根拠: 結果は親チェック単位でのみ読む不変の記録で、項目単位の横断検索要件がない。
    results        jsonb,
    ai_model       text,
    error_message  text,
    created_by     text NOT NULL DEFAULT '',
    created_at     timestamptz NOT NULL DEFAULT now(),
    checked_at     timestamptz,
    started_at     timestamptz
);

COMMENT ON TABLE  subsidiary_manual_check IS '副資材の手入力チェック実行記録（SoT: 本テーブル。記録系データ。再実行での巻き戻し禁止・原則2）';
COMMENT ON COLUMN subsidiary_manual_check.product_id     IS '対象商品（任意）。商品マスタ削除時も記録は温存する（SET NULL）';
COMMENT ON COLUMN subsidiary_manual_check.product_label  IS '表示用スナップショット（品番等）。商品マスタ削除後も履歴表示できるよう保持する';
COMMENT ON COLUMN subsidiary_manual_check.pattern_id     IS '使用したタグパターン（任意）。パターン削除時も記録は温存する（SET NULL）';
COMMENT ON COLUMN subsidiary_manual_check.pattern_name   IS '実行時のタグパターン名スナップショット（パターン削除・改名後の履歴表示用）';
COMMENT ON COLUMN subsidiary_manual_check.status         IS 'チェック状態: processing / completed / failed';
COMMENT ON COLUMN subsidiary_manual_check.judgment       IS '判定: pass=合格 / warn=要確認 / fail=要修正（completed 時のみ）';
COMMENT ON COLUMN subsidiary_manual_check.input          IS '手入力スナップショット（項目定義＋入力値。API ワイヤ形式と同一の JSON）';
COMMENT ON COLUMN subsidiary_manual_check.results        IS '項目単位の突合結果（API ワイヤ形式と同一の JSON）。非正規化。completed 時のみ';
COMMENT ON COLUMN subsidiary_manual_check.ai_model       IS 'タグ読取に使用した AI モデル ID（表示用）';
COMMENT ON COLUMN subsidiary_manual_check.started_at     IS '最後に AI 実行を開始した日時。孤児判定（滞留超過）とクレームの基準時刻';

CREATE INDEX IF NOT EXISTS ix_subsidiary_manual_check_created
    ON subsidiary_manual_check (created_at DESC);

CREATE TABLE IF NOT EXISTS subsidiary_manual_check_image (
    image_id      uuid PRIMARY KEY,
    check_id      uuid NOT NULL REFERENCES subsidiary_manual_check(check_id) ON DELETE CASCADE,
    file_name     text NOT NULL,
    content_type  text NOT NULL,
    size_bytes    bigint NOT NULL,
    data          bytea NOT NULL,
    sort_order    int NOT NULL DEFAULT 0
);

COMMENT ON TABLE  subsidiary_manual_check_image IS '手入力チェックのタグ画像原本。チェック（subsidiary_manual_check）に従属し連鎖削除される';
COMMENT ON COLUMN subsidiary_manual_check_image.content_type IS 'image/jpeg または image/png';
COMMENT ON COLUMN subsidiary_manual_check_image.sort_order   IS 'タグ画像の表示順（アップロード順）';

CREATE INDEX IF NOT EXISTS ix_subsidiary_manual_check_image_check
    ON subsidiary_manual_check_image (check_id);

-- 添付キャプチャ相当の既定パターン（品番/身長/胸囲/サイズ/組成/洗濯表示/補足/販売元/問合せ先/電話/原産国）を
-- 初期投入する。冪等性は WHERE NOT EXISTS（＝パターンが1件でもあれば投入しない）で担保し、
-- 運用中に追加・編集したパターンやシード自体の編集を再適用で巻き戻さない（原則2）。
-- pattern_id の固定 UUID は冪等判定には関与せず、シード行を後から安定して識別するための固定値。
INSERT INTO subsidiary_tag_pattern (pattern_id, name, description, fields, created_by, updated_by)
SELECT
    '00000000-0000-0000-0000-0000000000c1'::uuid,
    '標準タグ（un.deux 下げ札）',
    '添付の下げ札レイアウトに準拠した既定パターン。必要に応じて複製・編集してください。',
    $json$[
      {"key":"productNumber","label":"品番","valueType":"string","multiple":false,"compare":"text","required":true},
      {"key":"height","label":"身長","valueType":"string","multiple":false,"compare":"text","required":false},
      {"key":"chest","label":"胸囲","valueType":"string","multiple":false,"compare":"text","required":false},
      {"key":"size","label":"サイズ","valueType":"string","multiple":false,"compare":"text","required":false},
      {"key":"composition","label":"組成表示","valueType":"string","multiple":true,"compare":"ratio","required":false},
      {"key":"careLabels","label":"洗濯表示","valueType":"string","multiple":true,"compare":"careIcon","required":false},
      {"key":"note","label":"補足説明","valueType":"string","multiple":false,"compare":"text","required":false},
      {"key":"seller","label":"販売元","valueType":"string","multiple":false,"compare":"text","required":false},
      {"key":"contact","label":"お問い合わせ先","valueType":"string","multiple":false,"compare":"text","required":false},
      {"key":"phone","label":"電話","valueType":"string","multiple":false,"compare":"text","required":false},
      {"key":"originCountry","label":"原産国","valueType":"string","multiple":false,"compare":"text","required":false}
    ]$json$::jsonb,
    'system', 'system'
WHERE NOT EXISTS (SELECT 1 FROM subsidiary_tag_pattern);

-- ============================================================
--  ナレッジストア（RAG）: knowledge スキーマ
-- ------------------------------------------------------------
--  設計思想は docs/platform-design/detailed-design/DD-04（根拠必須・SoT→派生の
--  一方向・グレースフルデグラデーション）を単一テナント構成へ簡素化して適用する。
--  SoT: knowledge.entry（原本テキスト／原本ファイル bytea）。
--  派生: knowledge.chunk（チャンク） / knowledge.chunk_embedding（ベクター）。
--        派生は entry から常時再生成可能（再インデックス）。
--  ベクターは外部埋め込み API に依存しない決定的なローカルモデル
--  （文字 n-gram ハッシュベクトル。実装 SoT は backend Core の HashingVectorizer）
--  を real[] で保持する。(chunk_id, model) キーにより将来の外部埋め込み
--  モデル（pgvector 等）と共存・移行可能（DD-04 §3.3 の下位互換方針）。
-- ============================================================

-- 日本語の部分一致検索（word_similarity）用。contrib 標準の pg_trgm を使う。
-- 権限等で作成できない環境でもスキーマ適用全体は失敗させない（原則4）。
-- 拡張が無い場合、アプリ側は字句スコアを除外しベクトルスコアのみで検索する。
DO $$
BEGIN
    CREATE EXTENSION IF NOT EXISTS pg_trgm;
EXCEPTION WHEN OTHERS THEN
    RAISE NOTICE 'pg_trgm 拡張を作成できませんでした（%）。字句類似検索なしで動作します。', SQLERRM;
END
$$;

CREATE SCHEMA IF NOT EXISTS knowledge;

-- ナレッジ原本（SoT）。自由入力テキストまたはアップロードファイル＋抽出テキスト。
CREATE TABLE IF NOT EXISTS knowledge.entry (
    id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    scope              text NOT NULL CHECK (scope IN ('operator', 'user')),
    category           text NOT NULL CHECK (category IN ('group', 'business_type', 'department', 'basic', 'manual')),
    business_type_code text REFERENCES business_type (code),
    dept_code          text,
    biz_domain         text CHECK (biz_domain IN ('system', 'quality', 'logistics')),
    title              text NOT NULL,
    content            text NOT NULL DEFAULT '',
    source_type        text NOT NULL DEFAULT 'text' CHECK (source_type IN ('text', 'file')),
    file_name          text,
    file_content_type  text,
    file_size_bytes    bigint,
    file_data          bytea,
    index_state        text NOT NULL DEFAULT 'pending' CHECK (index_state IN ('pending', 'indexed', 'failed')),
    index_error        text,
    chunk_count        integer NOT NULL DEFAULT 0,
    seed_slug          text UNIQUE,
    status             text NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'deleted')),
    created_by         text NOT NULL,
    created_at         timestamptz NOT NULL DEFAULT now(),
    updated_by         text NOT NULL,
    updated_at         timestamptz NOT NULL DEFAULT now(),
    -- カテゴリ整合（原則6: 型・制約・SoT 宣言の同時更新）
    CHECK (category <> 'business_type' OR business_type_code IS NOT NULL),
    CHECK (category <> 'department' OR (business_type_code IS NOT NULL AND dept_code IS NOT NULL)),
    CHECK (category <> 'manual' OR scope = 'operator'),
    CHECK (source_type <> 'file' OR file_name IS NOT NULL)
);
COMMENT ON TABLE  knowledge.entry IS 'ナレッジ原本（SoT）。チャンク・ベクターは本テーブルから再生成可能な派生';
COMMENT ON COLUMN knowledge.entry.scope       IS 'operator=運営者RAG設定 / user=ユーザーRAG設定';
COMMENT ON COLUMN knowledge.entry.category    IS 'group=しまむらグループ / business_type=業態 / department=部門 / basic=基本情報 / manual=マニュアル（operator 専用）';
COMMENT ON COLUMN knowledge.entry.biz_domain  IS '業務チャットの部署絞込タグ（system/quality/logistics。NULL=共通）';
COMMENT ON COLUMN knowledge.entry.content     IS 'AI 参照用テキスト（自由入力 or ファイル抽出結果）。ファイルの原本は file_data に温存';
COMMENT ON COLUMN knowledge.entry.seed_slug   IS '事前蓄積ナレッジの冪等キー。NULL=画面からの手動登録。シードは ON CONFLICT DO NOTHING で再投入されない';
COMMENT ON COLUMN knowledge.entry.status      IS 'active/deleted。シード由来は論理削除（再起動時の再出現を防ぐ・原則2）';

CREATE INDEX IF NOT EXISTS ix_knowledge_entry_scope_category
    ON knowledge.entry (scope, category, status);
CREATE INDEX IF NOT EXISTS ix_knowledge_entry_business_type
    ON knowledge.entry (business_type_code) WHERE business_type_code IS NOT NULL;

-- チャンク（派生。entry 削除で連鎖削除）
CREATE TABLE IF NOT EXISTS knowledge.chunk (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    entry_id   uuid NOT NULL REFERENCES knowledge.entry (id) ON DELETE CASCADE,
    seq        integer NOT NULL,
    content    text NOT NULL,
    char_count integer NOT NULL,
    UNIQUE (entry_id, seq)
);
COMMENT ON TABLE knowledge.chunk IS 'ナレッジチャンク（派生）。見出し・ページ境界を尊重した分割。SoT は knowledge.entry';

-- 字句類似検索の索引（pg_trgm がある場合のみ）。
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pg_trgm') THEN
        CREATE INDEX IF NOT EXISTS ix_knowledge_chunk_trgm
            ON knowledge.chunk USING gin (content gin_trgm_ops);
    END IF;
END
$$;

-- チャンクベクター（派生）。model 列で埋め込みモデルの新旧共存・移行を可能にする。
CREATE TABLE IF NOT EXISTS knowledge.chunk_embedding (
    chunk_id bigint NOT NULL REFERENCES knowledge.chunk (id) ON DELETE CASCADE,
    model    text NOT NULL,
    dim      integer NOT NULL,
    vector   real[] NOT NULL,
    PRIMARY KEY (chunk_id, model)
);
COMMENT ON TABLE knowledge.chunk_embedding IS 'チャンク埋め込みベクトル（派生・L2正規化済み）。実装 SoT は Core の HashingVectorizer';

-- コサイン類似度（ベクトルは書込時に L2 正規化済みのため実質は内積）。
CREATE OR REPLACE FUNCTION knowledge.cosine_similarity(a real[], b real[])
RETURNS double precision
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT CASE
        WHEN s.na = 0 OR s.nb = 0 THEN 0
        ELSE s.dot / (sqrt(s.na) * sqrt(s.nb))
    END
    FROM (
        SELECT COALESCE(sum(x * y), 0) AS dot,
               COALESCE(sum(x * x), 0) AS na,
               COALESCE(sum(y * y), 0) AS nb
        FROM unnest(a, b) AS t(x, y)
    ) s
$$;
COMMENT ON FUNCTION knowledge.cosine_similarity(real[], real[]) IS
    'コサイン類似度。次元不一致時は unnest の NULL 補完により重なった次元だけの部分コサインになるため、呼出側で model・dim を揃えて使用すること（検索側は chunk_embedding.model と dim の両方で結合する）';

COMMIT;
