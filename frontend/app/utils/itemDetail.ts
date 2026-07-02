/**
 * 商品詳細分析（定番／トレンド）の表示射影ロジック（純粋関数）。
 *
 * バックエンドは SKU × 週の素材（売数・在庫数・在日・当週売価・累計）を返し、本モジュールで
 *   - 売価変更（値下げ）週の検出、
 *   - 在日の色分け（〜30／31-60／61〜）、
 *   - 消化率（累計／プロパー／値下げ）の分解、
 *   - 表示範囲のサマリー、
 *   - クリップボード（Excel 貼り付け）用の TSV / HTML 生成
 * を行う。Vue 非依存の純粋関数のみで構成する（テスト容易性・再利用性）。
 */
import type { ItemDetailResponse, ItemDetailRow } from '~/types/api'
import { formatCurrency, formatDecimal, formatNumber, formatRatioAsPercent } from './format'

/** 表示モード。週別＝週を列展開、当週＝最新週スナップショット（売上参照ファイル準拠）。 */
export type ItemDetailMode = 'weekly' | 'current'

/** 表示用の週次1点（値下げ週フラグ付き）。 */
export interface ItemDetailViewPoint {
  week: string
  quantity: number
  stock: number
  stockDays: number
  salePrice: number
  /** 前週（売価のある直近週）比で売価が下がった週＝値下げ。 */
  isMarkdown: boolean
}

/** 表示用の SKU 1行（週次索引・消化率分解・値下げ週数を集約）。 */
export interface ItemDetailViewRow {
  /** 一意キー（業態|記号|品番|単品）。 */
  key: string
  gyotaiCode: string
  shohinKigou: string
  /**
   * 棚割1／棚割2。sales_weekly には存在するが item-detail レスポンスでは未提供のため、
   * 現状は自然キーから決定的に生成したモック値（実データ接続時はレスポンスの値へ置換する）。
   */
  tanawari1: string
  tanawari2: string
  hinbanCode: string
  tanpinCode: string
  hinmei: string
  colorName: string
  sizeName: string
  listPrice: number
  kisetsu: string
  donyuDate: string | null
  departmentName: string | null
  brand: string | null
  primaryImageUrl: string | null
  periodQuantity: number
  points: ItemDetailViewPoint[]
  /** 週 → 点の索引（週別マトリクスのセル参照用）。 */
  pointByWeek: Map<string, ItemDetailViewPoint>
  /** 最新週（points 末尾）の点。無ければ null。 */
  latest: ItemDetailViewPoint | null
  /** 消化率（累計＝cumSales/cumDelivery。0..1。算出不能は null）。 */
  overallSellThrough: number | null
  /** プロパー消化率（初回値下げ週より前に売れた割合で累計消化率を按分）。 */
  properSellThrough: number | null
  /** 値下げ消化率（初回値下げ週以降に売れた割合で累計消化率を按分）。 */
  markdownSellThrough: number | null
  /** 値下げ（売価変更）週の数。 */
  markdownCount: number
}

/** 在日の文字色クラス（〜30：赤 ／ 31-60：青 ／ 61〜：既定 ／ データなし：淡色）。 */
export function stockDaysColorClass(days: number | null | undefined): string {
  if (days === null || days === undefined || days <= 0) return 'text-slate-300'
  if (days <= 30) return 'text-rose-600 font-semibold'
  if (days <= 60) return 'text-sky-600 font-semibold'
  return 'text-slate-600'
}

/** ItemDetailRow の自然キー（業態×記号×品番×単品）。 */
function rowKey(row: ItemDetailRow): string {
  return `${row.gyotaiCode}|${row.shohinKigou}|${row.hinbanCode}|${row.tanpinCode}`
}

/** 決定的ハッシュ（文字列→32bit 符号なし整数）。モック値の安定生成に使う。 */
function hashString(value: string): number {
  let h = 2166136261
  for (let i = 0; i < value.length; i++) {
    h ^= value.charCodeAt(i)
    h = Math.imul(h, 16777619)
  }
  return h >>> 0
}

const TANAWARI1_POOL = ['A01', 'A02', 'A03', 'B01', 'B02', 'C01', 'C02', 'D01']
const TANAWARI2_POOL = ['L1', 'L2', 'M1', 'M2', 'R1', 'R2']

/**
 * 棚割1／棚割2 のモック値（品番3桁単位で決定的）。
 * item-detail レスポンスに棚割が無いため、自然キーから安定生成する。実データ接続時は置換する。
 */
function mockTanawari(row: ItemDetailRow): { tanawari1: string; tanawari2: string } {
  const seed = hashString(`${row.gyotaiCode}|${row.shohinKigou}|${row.hinbanCode}`)
  return {
    tanawari1: TANAWARI1_POOL[seed % TANAWARI1_POOL.length]!,
    tanawari2: TANAWARI2_POOL[(seed >>> 5) % TANAWARI2_POOL.length]!,
  }
}

/**
 * 1 SKU 分の週次点に値下げフラグを付与し、消化率（累計／プロパー／値下げ）を算出する。
 *
 * 値下げ判定: 売価のある直近週と比較し、当週売価が下がっていれば値下げ週（値上げは
 * サイクリックの別アイテムと見做し無視）。プロパー/値下げ消化率は、初回値下げ週の前後で
 * 売れた数量の比で累計消化率を按分する（尚良い分析。厳密なプロパー原単位が無いための近似）。
 */
function buildViewRow(row: ItemDetailRow): ItemDetailViewRow {
  const sorted = row.points.slice().sort((a, b) => (a.week < b.week ? -1 : a.week > b.week ? 1 : 0))

  // 値下げ検出は for ループで行う（.map クロージャ内の代入は外側の型フローに反映されず、
  // firstMarkdownWeek が常に null と推論されてしまうため）。
  let prevPrice: number | null = null
  let firstMarkdownWeek: string | null = null
  let markdownCount = 0
  const points: ItemDetailViewPoint[] = []
  for (const p of sorted) {
    let isMarkdown = false
    if (p.salePrice > 0) {
      if (prevPrice !== null && p.salePrice < prevPrice) {
        isMarkdown = true
        markdownCount += 1
        if (firstMarkdownWeek === null) firstMarkdownWeek = p.week
      }
      prevPrice = p.salePrice
    }
    points.push({ ...p, isMarkdown })
  }

  const pointByWeek = new Map(points.map((p) => [p.week, p]))
  const latest = points.length > 0 ? points[points.length - 1]! : null

  const overallSellThrough =
    row.cumulativeDelivery > 0 ? row.cumulativeSales / row.cumulativeDelivery : null

  let properSellThrough: number | null = null
  let markdownSellThrough: number | null = null
  if (overallSellThrough !== null) {
    if (firstMarkdownWeek === null) {
      properSellThrough = overallSellThrough
      markdownSellThrough = 0
    } else {
      let properSold = 0
      let markdownSold = 0
      for (const p of points) {
        if (p.week < firstMarkdownWeek) properSold += p.quantity
        else markdownSold += p.quantity
      }
      const total = properSold + markdownSold
      const properShare = total > 0 ? properSold / total : 1
      properSellThrough = overallSellThrough * properShare
      markdownSellThrough = overallSellThrough * (1 - properShare)
    }
  }

  const tanawari = mockTanawari(row)

  return {
    key: rowKey(row),
    gyotaiCode: row.gyotaiCode,
    shohinKigou: row.shohinKigou,
    tanawari1: tanawari.tanawari1,
    tanawari2: tanawari.tanawari2,
    hinbanCode: row.hinbanCode,
    tanpinCode: row.tanpinCode,
    hinmei: row.hinmei,
    colorName: row.colorName,
    sizeName: row.sizeName,
    listPrice: row.listPrice,
    kisetsu: row.kisetsu,
    donyuDate: row.donyuDate,
    departmentName: row.departmentName,
    brand: row.brand,
    primaryImageUrl: row.primaryImageUrl,
    periodQuantity: row.periodQuantity,
    points,
    pointByWeek,
    latest,
    overallSellThrough,
    properSellThrough,
    markdownSellThrough,
    markdownCount,
  }
}

/** レスポンスを表示用ビュー（週リスト＋行）に変換する。 */
export function buildItemDetailView(resp: ItemDetailResponse): {
  weeks: string[]
  rows: ItemDetailViewRow[]
  latestWeek: string | null
  truncated: boolean
} {
  return {
    weeks: resp.weeks,
    rows: resp.rows.map(buildViewRow),
    latestWeek: resp.latestWeek,
    truncated: resp.truncated,
  }
}

/** 表示範囲のサマリー（工夫要素④）。 */
export interface ItemDetailSummary {
  skuCount: number
  totalQuantity: number
  totalStock: number
  averageStockDays: number | null
  averageSellThrough: number | null
  markdownSkuCount: number
}

/** 表示中の行からサマリーを算出する（在日・消化率は最新週基準の単純平均）。 */
export function computeItemDetailSummary(rows: readonly ItemDetailViewRow[]): ItemDetailSummary {
  if (rows.length === 0) {
    return {
      skuCount: 0,
      totalQuantity: 0,
      totalStock: 0,
      averageStockDays: null,
      averageSellThrough: null,
      markdownSkuCount: 0,
    }
  }
  let totalQuantity = 0
  let totalStock = 0
  let stockDaysSum = 0
  let stockDaysCount = 0
  let sellSum = 0
  let sellCount = 0
  let markdownSkuCount = 0
  for (const row of rows) {
    totalQuantity += row.periodQuantity
    if (row.latest) {
      totalStock += row.latest.stock
      if (row.latest.stockDays > 0) {
        stockDaysSum += row.latest.stockDays
        stockDaysCount += 1
      }
    }
    if (row.overallSellThrough !== null) {
      sellSum += row.overallSellThrough
      sellCount += 1
    }
    if (row.markdownCount > 0) markdownSkuCount += 1
  }
  return {
    skuCount: rows.length,
    totalQuantity,
    totalStock,
    averageStockDays: stockDaysCount > 0 ? stockDaysSum / stockDaysCount : null,
    averageSellThrough: sellCount > 0 ? sellSum / sellCount : null,
    markdownSkuCount,
  }
}

// ============================================================
// 週別マトリクスの行区分（売数・在庫数・在日・販売価格・気温）
// 気温は全SKU共通の週次値（東京/札幌/沖縄）。表示/非表示は呼び出し側（localStorage 保存）で管理する。
// ============================================================

/** 週別マトリクスの行区分キー。 */
export type ItemDetailRowCategory =
  | 'quantity'
  | 'stock'
  | 'stockDays'
  | 'salePrice'
  | 'tempTokyo'
  | 'tempSapporo'
  | 'tempOkinawa'

/** 行区分のカタログ情報。 */
export interface ItemDetailRowCategoryInfo {
  key: ItemDetailRowCategory
  label: string
  /** 気温行（全SKU共通の週次気温。SKU 明細に依存しない）か。 */
  isTemperature: boolean
}

/** 行区分カタログ（SoT・表示順）。デフォルトは全表示。 */
export const ITEM_DETAIL_ROW_CATEGORIES: readonly ItemDetailRowCategoryInfo[] = [
  { key: 'quantity', label: '売数', isTemperature: false },
  { key: 'stock', label: '在庫数', isTemperature: false },
  { key: 'stockDays', label: '在日', isTemperature: false },
  { key: 'salePrice', label: '販売価格', isTemperature: false },
  { key: 'tempTokyo', label: '気温(東京)', isTemperature: true },
  { key: 'tempSapporo', label: '気温(札幌)', isTemperature: true },
  { key: 'tempOkinawa', label: '気温(沖縄)', isTemperature: true },
]

type TempCity = 'tokyo' | 'sapporo' | 'okinawa'

// 各都市の月別平年気温（1〜12月・℃）。気温行のモック生成に使う。
const MONTHLY_TEMP: Record<TempCity, number[]> = {
  tokyo: [5.2, 5.7, 8.7, 13.9, 18.2, 21.4, 25.0, 26.4, 22.8, 17.5, 12.1, 7.6],
  sapporo: [-3.6, -3.1, 0.6, 7.1, 12.4, 16.7, 20.5, 22.3, 18.1, 11.8, 4.9, -0.9],
  okinawa: [17.0, 17.1, 18.9, 21.4, 24.0, 26.8, 28.9, 28.7, 27.6, 25.2, 22.1, 18.7],
}

const TEMP_CITY_BY_CATEGORY: Partial<Record<ItemDetailRowCategory, TempCity>> = {
  tempTokyo: 'tokyo',
  tempSapporo: 'sapporo',
  tempOkinawa: 'okinawa',
}

/**
 * 週（yyyy-MM-dd）の都市別モック気温（℃・小数1桁）。
 * 月内は当月→翌月の平年値を日付で線形補間し、隣接週が滑らかに変化するようにする。
 * item-detail レスポンスに気温が無いためのモック（実測 dim_climate 接続時は置換する）。
 */
export function mockWeeklyTemperature(week: string, city: TempCity): number {
  const parts = week.split('-')
  const m = Number.parseInt(parts[1] ?? '1', 10)
  const d = Number.parseInt(parts[2] ?? '1', 10)
  const months = MONTHLY_TEMP[city]
  const idx = ((m - 1) % 12 + 12) % 12
  const base = months[idx] ?? 0
  const next = months[m % 12] ?? base
  const frac = Math.min(1, Math.max(0, (d - 1) / 31))
  return Math.round((base + (next - base) * frac) * 10) / 10
}

/**
 * 行区分×SKU×週のセル表示文字列。気温は週共通、それ以外は該当週の点（無ければ空欄）。
 * 表示・クリップボードの双方から使い、表現の一貫性を保つ。
 */
export function itemDetailCellText(
  category: ItemDetailRowCategory,
  row: ItemDetailViewRow,
  week: string,
): string {
  const city = TEMP_CITY_BY_CATEGORY[category]
  if (city) {
    return formatDecimal(mockWeeklyTemperature(week, city), 1)
  }
  const p = row.pointByWeek.get(week)
  if (!p) return ''
  switch (category) {
    case 'quantity':
      return formatNumber(p.quantity)
    case 'stock':
      return formatNumber(p.stock)
    case 'stockDays':
      return p.stockDays > 0 ? formatDecimal(p.stockDays, 1) : ''
    case 'salePrice':
      return p.salePrice > 0 ? formatCurrency(p.salePrice) : ''
    default:
      return ''
  }
}

// ============================================================
// クリップボード（Excel 貼り付け）用の TSV / HTML 生成
// utils/clipboard.ts の copyHtmlToClipboard に渡す。HTML は自前でエスケープする（契約）。
// ============================================================

/** HTML 特殊文字をエスケープする（自己 XSS 防止。copyHtmlToClipboard の契約を満たす）。 */
function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
}

/** 在日セルの Excel 用文字色（画面の色分けに対応）。 */
function stockDaysHtmlColor(days: number): string {
  if (days <= 0) return '#cbd5e1'
  if (days <= 30) return '#e11d48'
  if (days <= 60) return '#0284c7'
  return '#475569'
}

const TD = 'border:1px solid #e2e8f0;padding:2px 6px'
const TH = `${TD};background-color:#f1f5f9;font-weight:600`

const CATEGORY_INFO_BY_KEY: ReadonlyMap<ItemDetailRowCategory, ItemDetailRowCategoryInfo> = new Map(
  ITEM_DETAIL_ROW_CATEGORIES.map((c) => [c.key, c]),
)

/** 行区分キーの表示ラベル（未知キーはそのまま返す）。 */
export function itemDetailRowCategoryLabel(key: ItemDetailRowCategory): string {
  return CATEGORY_INFO_BY_KEY.get(key)?.label ?? key
}

/**
 * 週別マトリクスの TSV / HTML を生成する（SKU ごとに、表示中の行区分ぶんの行）。
 * メタ列は 商品記号／棚割1／棚割2 を含む。表示中の行区分（categories）は画面と一致させる。
 */
export function buildWeeklyClipboard(
  weeks: readonly string[],
  rows: readonly ItemDetailViewRow[],
  categories: readonly ItemDetailRowCategory[],
): { html: string; text: string } {
  const cats = categories.length > 0 ? categories : ITEM_DETAIL_ROW_CATEGORIES.map((c) => c.key)
  const metaHeaders = ['品番', '単品', '商品記号', '棚割1', '棚割2', '品名', 'カラー', 'サイズ', '上代']
  const headerCells = [...metaHeaders, '区分', ...weeks]

  const textLines: string[] = [headerCells.join('\t')]
  const htmlRows: string[] = [
    `<tr>${headerCells.map((h) => `<th style="${TH}">${escapeHtml(h)}</th>`).join('')}</tr>`,
  ]

  for (const row of rows) {
    const meta = [
      row.hinbanCode,
      row.tanpinCode,
      row.shohinKigou,
      row.tanawari1,
      row.tanawari2,
      row.hinmei,
      row.colorName,
      row.sizeName,
      String(row.listPrice),
    ]
    for (let i = 0; i < cats.length; i++) {
      const cat = cats[i]!
      // メタ列は 1 行目のみ値、2 行目以降は空欄にして Excel の縦連結を模す。
      const metaCells = i === 0 ? meta : meta.map(() => '')
      const weekCells = weeks.map((w) => {
        const p = row.pointByWeek.get(w)
        const text = itemDetailCellText(cat, row, w)
        let color: string | undefined
        let markdown = false
        if (cat === 'stockDays' && p && p.stockDays > 0) color = stockDaysHtmlColor(p.stockDays)
        else if (cat === 'quantity' && p?.isMarkdown) markdown = true
        return { text, color, markdown }
      })
      textLines.push([...metaCells, itemDetailRowCategoryLabel(cat), ...weekCells.map((c) => c.text)].join('\t'))
      const htmlMeta = metaCells.map((m) => `<td style="${TD}">${escapeHtml(m)}</td>`).join('')
      const htmlKind = `<td style="${TH}">${escapeHtml(itemDetailRowCategoryLabel(cat))}</td>`
      const htmlWeeks = weekCells
        .map((c) => {
          let style = `${TD};text-align:right`
          if (c.color) style += `;color:${c.color}`
          if (c.markdown) style += ';background-color:#fee2e2'
          return `<td style="${style}">${escapeHtml(c.text)}</td>`
        })
        .join('')
      htmlRows.push(`<tr>${htmlMeta}${htmlKind}${htmlWeeks}</tr>`)
    }
  }

  const html = `<meta charset="utf-8"><table style="border-collapse:collapse">${htmlRows.join('')}</table>`
  return { html, text: textLines.join('\n') }
}

/** 当週テーブルの TSV / HTML を生成する（1 SKU 1 行・売上参照ファイル準拠）。 */
export function buildCurrentClipboard(rows: readonly ItemDetailViewRow[]): {
  html: string
  text: string
} {
  const headers = [
    '品番', '単品', '記号', '品名', 'カラー', 'サイズ', '上代', '導入日',
    '売数', '在庫数', '在日', '消化率', 'プロパー消化率', '値下げ消化率',
  ]
  const textLines: string[] = [headers.join('\t')]
  const htmlRows: string[] = [
    `<tr>${headers.map((h) => `<th style="${TH}">${escapeHtml(h)}</th>`).join('')}</tr>`,
  ]

  for (const row of rows) {
    const cells = [
      row.hinbanCode,
      row.tanpinCode,
      row.shohinKigou,
      row.hinmei,
      row.colorName,
      row.sizeName,
      String(row.listPrice),
      row.donyuDate ?? '',
      row.latest ? String(row.latest.quantity) : '',
      row.latest ? String(row.latest.stock) : '',
      row.latest && row.latest.stockDays > 0 ? formatDecimal(row.latest.stockDays, 1) : '',
      row.overallSellThrough !== null ? formatRatioAsPercent(row.overallSellThrough) : '',
      row.properSellThrough !== null ? formatRatioAsPercent(row.properSellThrough) : '',
      row.markdownSellThrough !== null ? formatRatioAsPercent(row.markdownSellThrough) : '',
    ]
    textLines.push(cells.join('\t'))
    const stockDaysIdx = 10
    const html = cells
      .map((c, i) => {
        if (i === stockDaysIdx && row.latest && row.latest.stockDays > 0) {
          return `<td style="${TD};color:${stockDaysHtmlColor(row.latest.stockDays)}">${escapeHtml(c)}</td>`
        }
        return `<td style="${TD}">${escapeHtml(c)}</td>`
      })
      .join('')
    htmlRows.push(`<tr>${html}</tr>`)
  }

  const html = `<meta charset="utf-8"><table style="border-collapse:collapse">${htmlRows.join('')}</table>`
  return { html, text: textLines.join('\n') }
}

/** サマリー値の表示用フォーマット（テンプレートの重複を避けるヘルパ）。 */
export function formatSummary(summary: ItemDetailSummary): {
  totalQuantity: string
  totalStock: string
  averageStockDays: string
  averageSellThrough: string
} {
  return {
    totalQuantity: formatNumber(summary.totalQuantity),
    totalStock: formatNumber(summary.totalStock),
    averageStockDays: summary.averageStockDays === null ? '—' : formatDecimal(summary.averageStockDays, 1),
    averageSellThrough:
      summary.averageSellThrough === null ? '—' : formatRatioAsPercent(summary.averageSellThrough),
  }
}
