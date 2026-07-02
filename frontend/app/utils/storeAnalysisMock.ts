/**
 * 店舗分析（小売モード）のモックデータと集計ロジック（純粋関数）。
 *
 * 現状、当システムは店舗（取引先）粒度の実績を持つが「店舗属性（売上上位/下位・エリア・
 * 店舗タイプ）」「発注タイプ（定番=自動発注 / トレンド・季節=本部/店舗発注 / 地元産直=店舗発注）」
 * といった小売本部視点の属性を保持していない。そこで、これらの属性を組み合わせて
 * 「条件内で売れている商品ランキング」を出す体験をモックデータで先行して表現する。
 *
 * 決定的（seed 付きハッシュ）に生成し、SSR とクライアントで同一結果になるようにする
 * （Math.random/Date を使わない＝ハイドレーション不整合を避ける）。実データ接続時は
 * 本モジュールを店舗ディメンション＋発注区分を持つ集計 API 呼び出しへ置き換える。
 */

/** 発注タイプ（役割・権限つき）。ヒントの分類に対応する。 */
export interface OrderTypeInfo {
  key: OrderTypeKey
  label: string
  /** 発注の役割・権限（誰が発注するか）。 */
  authority: string
}
export type OrderTypeKey = 'teiban' | 'trend' | 'season' | 'local'

export const STORE_ORDER_TYPES: readonly OrderTypeInfo[] = [
  { key: 'teiban', label: '定番', authority: '自動発注' },
  { key: 'trend', label: 'トレンド', authority: '本部発注／店舗発注' },
  { key: 'season', label: '季節', authority: '本部発注／店舗発注' },
  { key: 'local', label: '地元産直', authority: '店舗発注' },
]

/** 店舗の売上ランク（上位店／下位店）。 */
export type StoreSalesTier = 'top' | 'bottom'
/** 店舗タイプ1（立地）。 */
export type StoreType1 = 'urban' | 'standard' | 'rural'
/** 店舗タイプ2（形態）。 */
export type StoreType2 = 'mall' | 'street'

export const STORE_TIERS: readonly { key: StoreSalesTier; label: string }[] = [
  { key: 'top', label: '上位店' },
  { key: 'bottom', label: '下位店' },
]
export const STORE_TYPE1: readonly { key: StoreType1; label: string }[] = [
  { key: 'urban', label: '都心' },
  { key: 'standard', label: '標準' },
  { key: 'rural', label: '地方' },
]
export const STORE_TYPE2: readonly { key: StoreType2; label: string }[] = [
  { key: 'mall', label: 'モール' },
  { key: 'street', label: '路面店舗' },
]

/** エリア（近隣グループを構成する店舗の所在）。 */
export const STORE_AREAS: readonly { key: string; label: string }[] = [
  { key: 'oizumi', label: '大泉' },
  { key: 'asaka', label: '朝霞' },
  { key: 'wako', label: '和光' },
]

/** モック店舗1件。 */
export interface MockStore {
  code: string
  name: string
  area: string
  salesTier: StoreSalesTier
  type1: StoreType1
  type2: StoreType2
}

/** モック商品1件。 */
export interface MockStoreProduct {
  key: string
  name: string
  departmentCode: string
  departmentName: string
  /** 服種3桁CD（商品カテゴリ中）。 */
  hinbanCode: string
  orderType: OrderTypeKey
  listPrice: number
}

/** 商品カテゴリ大（部門）。 */
export const STORE_DEPARTMENTS: readonly { code: string; name: string }[] = [
  { code: '11', name: 'レディース' },
  { code: '12', name: 'メンズ' },
  { code: '31', name: 'キッズ' },
  { code: '51', name: 'インナー・靴下' },
  { code: '56', name: '生活雑貨' },
]

/** 決定的ハッシュ（文字列→32bit 符号なし整数）。seed 付き擬似乱数の種に使う。 */
function hashString(value: string): number {
  let h = 2166136261
  for (let i = 0; i < value.length; i++) {
    h ^= value.charCodeAt(i)
    h = Math.imul(h, 16777619)
  }
  return h >>> 0
}

/** 種から 0..1 の決定的な擬似乱数を返す（mulberry32 の1ステップ）。 */
function seededUnit(seed: number): number {
  let t = (seed + 0x6d2b79f5) >>> 0
  t = Math.imul(t ^ (t >>> 15), t | 1)
  t ^= t + Math.imul(t ^ (t >>> 7), t | 61)
  return ((t ^ (t >>> 14)) >>> 0) / 4294967296
}

// 店舗名・服種名の語彙（決定的に組み合わせる）。
const HINBAN_BY_DEPT: Record<string, { code: string; name: string }[]> = {
  '11': [
    { code: '558', name: 'レギンスパンツ' },
    { code: '436', name: 'デニムワイドパンツ' },
    { code: '612', name: 'カットソー' },
    { code: '703', name: 'ニットカーディガン' },
  ],
  '12': [
    { code: '221', name: 'チノパンツ' },
    { code: '318', name: 'ポロシャツ' },
    { code: '905', name: 'スウェットパーカー' },
  ],
  '31': [
    { code: '140', name: 'キッズTシャツ' },
    { code: '162', name: 'キッズパンツ' },
  ],
  '51': [
    { code: '480', name: 'ソックス' },
    { code: '492', name: 'インナーシャツ' },
  ],
  '56': [
    { code: '870', name: 'タオル' },
    { code: '884', name: '収納ケース' },
  ],
}

const ORDER_TYPE_KEYS: OrderTypeKey[] = ['teiban', 'trend', 'season', 'local']

/** モック店舗一覧（決定的・固定）。近隣3エリア × 立地/形態のバリエーション。 */
export function buildMockStores(): MockStore[] {
  const stores: MockStore[] = []
  const type1s: StoreType1[] = ['urban', 'standard', 'rural']
  const type2s: StoreType2[] = ['mall', 'street']
  for (const area of STORE_AREAS) {
    // 各エリア3店舗（属性を散らす）。
    for (let i = 0; i < 3; i++) {
      const seed = hashString(`store|${area.key}|${i}`)
      const type1 = type1s[Math.floor(seededUnit(seed) * type1s.length)] ?? 'standard'
      const type2 = type2s[Math.floor(seededUnit(seed + 7) * type2s.length)] ?? 'street'
      // 売上ランクは店舗ごとの規模係数（後段のランキングと整合）で上位/下位を決める。
      const scale = seededUnit(seed + 13)
      const salesTier: StoreSalesTier = scale >= 0.5 ? 'top' : 'bottom'
      const suffix = type2 === 'mall' ? 'モール店' : `${i + 1}号店`
      stores.push({
        code: `${area.key}-${i + 1}`,
        name: `${area.label}${suffix}`,
        area: area.key,
        salesTier,
        type1,
        type2,
      })
    }
  }
  return stores
}

/** モック商品一覧（決定的・固定）。部門×服種×発注タイプで散らす。 */
export function buildMockProducts(): MockStoreProduct[] {
  const products: MockStoreProduct[] = []
  for (const dept of STORE_DEPARTMENTS) {
    const hinbans = HINBAN_BY_DEPT[dept.code] ?? []
    for (const hinban of hinbans) {
      // 服種ごとに2〜3商品（カラー/シリーズ違いを模す）。
      for (let v = 0; v < 3; v++) {
        const key = `${dept.code}-${hinban.code}-${v}`
        const seed = hashString(`prod|${key}`)
        const orderType = ORDER_TYPE_KEYS[Math.floor(seededUnit(seed) * ORDER_TYPE_KEYS.length)] ?? 'teiban'
        const listPrice = 780 + Math.floor(seededUnit(seed + 3) * 12) * 200
        const variant = ['A', 'B', 'C'][v] ?? 'A'
        products.push({
          key,
          name: `${hinban.name} ${variant}`,
          departmentCode: dept.code,
          departmentName: dept.name,
          hinbanCode: hinban.code,
          orderType,
          listPrice,
        })
      }
    }
  }
  return products
}

/** 店舗分析フィルタ状態。空/all は「すべて」。 */
export interface StoreAnalysisFilter {
  orderTypes: OrderTypeKey[]
  salesTier: StoreSalesTier | 'all'
  area: string | 'all'
  type1: StoreType1 | 'all'
  type2: StoreType2 | 'all'
  departmentCode: string | 'all'
  hinbanCode: string | 'all'
}

export function defaultStoreAnalysisFilter(): StoreAnalysisFilter {
  return {
    orderTypes: [],
    salesTier: 'all',
    area: 'all',
    type1: 'all',
    type2: 'all',
    departmentCode: 'all',
    hinbanCode: 'all',
  }
}

/** 集計後のランキング1行。 */
export interface StoreRankingRow {
  rank: number
  product: MockStoreProduct
  /** 選択店舗合計の売上数量。 */
  quantity: number
  /** 売上金額（数量 × 上代）。 */
  amount: number
  /** 実績に寄与した店舗数。 */
  storeCount: number
  /** モック消化率（0..1）。 */
  sellThroughRate: number
  /** 店頭在庫数（モック）。 */
  stock: number
}

/** 店舗が店舗属性フィルタに合致するか。 */
function storeMatches(store: MockStore, filter: StoreAnalysisFilter): boolean {
  if (filter.salesTier !== 'all' && store.salesTier !== filter.salesTier) return false
  if (filter.area !== 'all' && store.area !== filter.area) return false
  if (filter.type1 !== 'all' && store.type1 !== filter.type1) return false
  if (filter.type2 !== 'all' && store.type2 !== filter.type2) return false
  return true
}

/** 商品が商品属性・発注タイプフィルタに合致するか。 */
function productMatches(product: MockStoreProduct, filter: StoreAnalysisFilter): boolean {
  if (filter.orderTypes.length > 0 && !filter.orderTypes.includes(product.orderType)) return false
  if (filter.departmentCode !== 'all' && product.departmentCode !== filter.departmentCode) return false
  if (filter.hinbanCode !== 'all' && product.hinbanCode !== filter.hinbanCode) return false
  return true
}

/** 発注タイプ別の売れ行き係数（定番=安定高、地元産直=局所的）。 */
const ORDER_TYPE_FACTOR: Record<OrderTypeKey, number> = {
  teiban: 1.3,
  trend: 1.1,
  season: 1.0,
  local: 0.7,
}

/**
 * フィルタ条件で「売れている商品ランキング」を集計する（モック）。
 * 各 (店舗 × 商品) の基礎需要を決定的に生成し、選択店舗で合算して降順に並べる。
 */
export function buildStoreRanking(
  stores: readonly MockStore[],
  products: readonly MockStoreProduct[],
  filter: StoreAnalysisFilter,
  limit = 20,
): { rows: StoreRankingRow[]; storeCount: number } {
  const targetStores = stores.filter((s) => storeMatches(s, filter))
  const targetProducts = products.filter((p) => productMatches(p, filter))

  const rows: StoreRankingRow[] = []
  for (const product of targetProducts) {
    let quantity = 0
    let contributingStores = 0
    let stockSum = 0
    for (const store of targetStores) {
      const seed = hashString(`sales|${store.code}|${product.key}`)
      const base = 20 + Math.floor(seededUnit(seed) * 180)
      // 上位店は需要が大きい。都心/モールも押し上げる。発注タイプ係数を掛ける。
      const tierFactor = store.salesTier === 'top' ? 1.4 : 0.7
      const locFactor = (store.type1 === 'urban' ? 1.15 : store.type1 === 'rural' ? 0.9 : 1)
        * (store.type2 === 'mall' ? 1.1 : 0.95)
      const qty = Math.round(base * tierFactor * locFactor * ORDER_TYPE_FACTOR[product.orderType])
      if (qty > 0) {
        quantity += qty
        contributingStores += 1
        stockSum += Math.round(qty * (0.3 + seededUnit(seed + 5) * 0.9))
      }
    }
    if (quantity <= 0) continue
    // 消化率は 売数 / (売数+在庫) の近似。
    const sellThroughRate = quantity + stockSum > 0 ? quantity / (quantity + stockSum) : 0
    rows.push({
      rank: 0,
      product,
      quantity,
      amount: quantity * product.listPrice,
      storeCount: contributingStores,
      sellThroughRate,
      stock: stockSum,
    })
  }

  rows.sort((a, b) => (b.quantity !== a.quantity ? b.quantity - a.quantity : a.product.key < b.product.key ? -1 : 1))
  const limited = rows.slice(0, limit)
  limited.forEach((row, i) => {
    row.rank = i + 1
  })
  return { rows: limited, storeCount: targetStores.length }
}
