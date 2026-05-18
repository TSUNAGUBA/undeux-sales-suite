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
-- 日次売上ビュー — 週次ファクトの日次列を縦持ちに展開
-- ------------------------------------------------------------
--  toshu_uriage_count1..7 を (sales_date, quantity) に展開する。
--  sales_date = import_date - 8 + day_index
--    day_index 1 → import_date-7（月） / day_index 7 → import_date-1（日）
-- ------------------------------------------------------------
CREATE OR REPLACE VIEW v_sales_daily AS
SELECT
    sw.id                                              AS sales_weekly_id,
    sw.import_batch_id,
    sw.import_date,
    (sw.import_date - 8 + d.day_index)::date           AS sales_date,
    d.day_index,
    sw.customer_code,
    sw.gyotai_code,
    sw.department,
    sw.hinban_code,
    sw.tanpin_code,
    sw.hinmei,
    sw.shohin_kigou,
    sw.color,
    sw.size,
    sw.kisetsu,
    d.quantity,
    sw.genka,
    sw.baika,
    (d.quantity * sw.baika)                            AS amount,
    (d.quantity * (sw.baika - sw.genka))               AS gross_profit
FROM sales_weekly sw
CROSS JOIN LATERAL (VALUES
    (1, sw.toshu_uriage_count1),
    (2, sw.toshu_uriage_count2),
    (3, sw.toshu_uriage_count3),
    (4, sw.toshu_uriage_count4),
    (5, sw.toshu_uriage_count5),
    (6, sw.toshu_uriage_count6),
    (7, sw.toshu_uriage_count7)
) AS d(day_index, quantity);

COMMENT ON VIEW v_sales_daily IS '日次売上ビュー。週次ファクトの日次7列を (sales_date, quantity) に縦展開';

COMMIT;
