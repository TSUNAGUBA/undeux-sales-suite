/**
 * 在庫マネジメントのモックデータ生成（純粋関数）。
 *
 * バックエンドが保持しない粒度・区分を、決定的（seed 付きハッシュ）なモックで先行表現する。
 *   - 商品記号ポジショニング: byShohinKigo が API 未提供のため、品番3桁別の健全性から商品記号へ展開する。
 *   - 残在庫（倉庫在庫）: 倉庫在庫・取置・先付は店頭在庫スナップショットに無いため、業態×部門×商品記号でモック生成する。
 *   - 発注区分（売発注／情報発注）: スタースキーマに区分が無いため、SKU 週次の変化をモック生成する。
 *
 * 実データ接続時は、店舗/倉庫ディメンション・発注区分を持つ集計 API へ置き換える。
 */
import type { InventoryDepartmentHealthRow } from '~/types/api'

/** 決定的ハッシュ（文字列→32bit 符号なし整数）。 */
export function hashString(value: string): number {
  let h = 2166136261
  for (let i = 0; i < value.length; i++) {
    h ^= value.charCodeAt(i)
    h = Math.imul(h, 16777619)
  }
  return h >>> 0
}

/** 種から 0..1 の決定的な擬似乱数（mulberry32 の1ステップ）。 */
export function seededUnit(seed: number): number {
  let t = (seed + 0x6d2b79f5) >>> 0
  t = Math.imul(t ^ (t >>> 15), t | 1)
  t ^= t + Math.imul(t ^ (t >>> 7), t | 61)
  return ((t ^ (t >>> 14)) >>> 0) / 4294967296
}

/**
 * 商品記号ポジショニングのモック行。
 * byShohinKigo は API 未提供のため、品番3桁別の健全性（byHinban）を各1〜3の商品記号へ
 * 決定的に按分し、消化率・在庫日数へ小さなばらつきを与える。実データ接続時は byShohinKigo に置換する。
 */
export function mockShohinKigoPositioning(
  byHinban: readonly InventoryDepartmentHealthRow[],
): InventoryDepartmentHealthRow[] {
  const rows: InventoryDepartmentHealthRow[] = []
  for (const h of byHinban) {
    const seed0 = hashString(h.key)
    const variants = 1 + (seed0 % 3)
    const weights: number[] = []
    let wsum = 0
    for (let i = 0; i < variants; i++) {
      const w = 0.5 + seededUnit(seed0 + i * 7)
      weights.push(w)
      wsum += w
    }
    for (let i = 0; i < variants; i++) {
      const seed = hashString(`${h.key}|sk|${i}`)
      const share = weights[i]! / wsum
      const sell = Math.min(1, Math.max(0, h.sellThroughRate + (seededUnit(seed) - 0.5) * 0.2))
      const days = Math.max(0, h.averageStockDays + (seededUnit(seed + 3) - 0.5) * 20)
      rows.push({
        key: `${h.key}-sk${i}`,
        // 商品記号らしいモックラベル（品番3桁 + 2桁）。
        label: `${h.key}${String(10 + (seed % 89))}`,
        stock: Math.round(h.stock * share),
        stockValueCost: Math.round(h.stockValueCost * share),
        sellThroughRate: sell,
        averageStockDays: days,
        healthyStock: Math.round(h.healthyStock * share),
        cautionStock: Math.round(h.cautionStock * share),
        stagnantStock: Math.round(h.stagnantStock * share),
        dormantStock: Math.round(h.dormantStock * share),
      })
    }
  }
  return rows
}

const HINMEI_POOL = [
  'デニムパンツ', 'レギンス', 'カットソー', 'ニットカーデ', 'スウェットパーカー',
  'ソックス', 'Tシャツ', 'フレアスカート', 'ワイドパンツ', 'インナーシャツ',
]

// ============================================================
// 残在庫（倉庫在庫）モック
// 倉庫在庫・取置・先付・発注は店頭在庫スナップショットに無いため、業態×部門で SKU を決定的に生成する。
// ============================================================

/** 残在庫（倉庫在庫）の1行（SKU 単位）。 */
export interface WarehouseRow {
  key: string
  businessTypeCode: string
  departmentCode: string
  shohinKigou: string
  hinbanCode: string
  tanpinCode: string
  hinmei: string
  /** 先付数（EDI）。 */
  sakizukeCount: number
  /** 発注数（EDI・小数1桁）。 */
  hatchuCount: number
  /** 累計納品数（基幹）。 */
  ruikeiNohinCount: number
  /** 累計売上数（基幹）。 */
  ruikeiUriageCount: number
  /** 在庫数（店頭在庫・基幹）。 */
  zaikosu: number
  /** 発注済未納品＝発注数−納品数（算出。負値は0）。 */
  orderNotDelivered: number
  /** 取置在庫（WMS）。 */
  reservedStock: number
  /** 倉庫在庫数（WMS）。 */
  warehouseStock: number
}

/**
 * 残在庫（倉庫在庫）のモックデータを業態×部門ごとに決定的に生成する。
 * 数量は 発注 ≥ 納品 ≥ 売上 の整合を保つ。実データ接続時は WMS/EDI/基幹の結合クエリへ置換する。
 */
export function buildWarehouseMock(
  businessTypeCodes: readonly string[],
  departmentCodes: readonly string[],
): WarehouseRow[] {
  const rows: WarehouseRow[] = []
  for (const bt of businessTypeCodes) {
    for (const dept of departmentCodes) {
      const kigoCount = 2 + (hashString(`wh|${bt}|${dept}`) % 2) // 2-3 商品記号
      for (let k = 0; k < kigoCount; k++) {
        const kigo = `LG${dept}${String(100 + (hashString(`${bt}|${dept}|${k}`) % 900))}`
        const hinban = String(100 + (hashString(`hb|${bt}|${dept}|${k}`) % 900))
        for (let t = 0; t < 2; t++) {
          const seed = hashString(`${bt}|${dept}|${k}|${t}`)
          const hatchu = Math.round((50 + seededUnit(seed) * 450) * 10) / 10
          const sakizuke = Math.round(hatchu * (0.1 + seededUnit(seed + 1) * 0.3))
          const nohin = Math.round(hatchu * (0.5 + seededUnit(seed + 2) * 0.5))
          const uriage = Math.round(nohin * (0.3 + seededUnit(seed + 3) * 0.6))
          const remain = Math.max(0, nohin - uriage)
          rows.push({
            key: `${bt}-${dept}-${k}-${t}`,
            businessTypeCode: bt,
            departmentCode: dept,
            shohinKigou: kigo,
            hinbanCode: hinban,
            tanpinCode: String(1000 + t),
            hinmei: HINMEI_POOL[seed % HINMEI_POOL.length]!,
            sakizukeCount: sakizuke,
            hatchuCount: hatchu,
            ruikeiNohinCount: nohin,
            ruikeiUriageCount: uriage,
            zaikosu: Math.round(remain * (0.4 + seededUnit(seed + 4) * 0.3)),
            orderNotDelivered: Math.max(0, Math.round(hatchu) - nohin),
            reservedStock: Math.round(seededUnit(seed + 6) * 20),
            warehouseStock: Math.round(remain * (0.2 + seededUnit(seed + 5) * 0.3)),
          })
        }
      }
    }
  }
  return rows
}

// ============================================================
// 発注区分（売発注／情報発注）モック
// スタースキーマに発注区分が無いため、SKU の週次区分と変化をモック生成する。
// 実データ接続時は public.sales-weekly を発注区分つきでスタースキーマに取り込み置換する。
// ============================================================

/** 発注区分（売発注／情報発注）。 */
export type OrderClass = 'sales' | 'info'

export const ORDER_CLASS_LABEL: Record<OrderClass, string> = {
  sales: '売発注',
  info: '情報発注',
}

/** 発注区分の週次履歴1点。 */
export interface OrderClassHistoryPoint {
  week: string
  orderClass: OrderClass
}

/** 発注区分の変化を表す1行（SKU 単位）。 */
export interface OrderClassRow {
  key: string
  businessTypeCode: string
  departmentCode: string
  shohinKigou: string
  hinbanCode: string
  tanpinCode: string
  hinmei: string
  /** 週次履歴（昇順）。 */
  history: OrderClassHistoryPoint[]
  /** 前週の区分（履歴が1点未満なら null）。 */
  previous: OrderClass | null
  /** 当週の区分（履歴が空なら null）。 */
  current: OrderClass | null
  /** 前週から当週で区分が変わったか。 */
  changed: boolean
  /** 履歴全体での変化回数。 */
  changeCount: number
}

/**
 * 発注区分（売発注／情報発注）の週次履歴をモック生成する。
 * 各 SKU は初期区分から、週ごとに小確率で切り替わる（区分は一定の粘着性を持つ）。
 */
export function buildOrderClassMock(
  businessTypeCodes: readonly string[],
  departmentCodes: readonly string[],
  weeks: readonly string[],
): OrderClassRow[] {
  const rows: OrderClassRow[] = []
  for (const bt of businessTypeCodes) {
    for (const dept of departmentCodes) {
      const kigoCount = 2 + (hashString(`oc|${bt}|${dept}`) % 2)
      for (let k = 0; k < kigoCount; k++) {
        const kigo = `LG${dept}${String(100 + (hashString(`oc|${bt}|${dept}|${k}`) % 900))}`
        const hinban = String(100 + (hashString(`ochb|${bt}|${dept}|${k}`) % 900))
        for (let t = 0; t < 2; t++) {
          const skuKey = `${bt}-${dept}-${k}-${t}`
          const seed0 = hashString(`ocsku|${skuKey}`)
          let cls: OrderClass = seededUnit(seed0) < 0.6 ? 'sales' : 'info'
          const history: OrderClassHistoryPoint[] = []
          let changeCount = 0
          weeks.forEach((week, wi) => {
            if (wi > 0 && seededUnit(hashString(`${skuKey}|${wi}`)) < 0.15) {
              cls = cls === 'sales' ? 'info' : 'sales'
              changeCount += 1
            }
            history.push({ week, orderClass: cls })
          })
          const current = history.length > 0 ? history[history.length - 1]!.orderClass : null
          const previous = history.length > 1 ? history[history.length - 2]!.orderClass : null
          rows.push({
            key: skuKey,
            businessTypeCode: bt,
            departmentCode: dept,
            shohinKigou: kigo,
            hinbanCode: hinban,
            tanpinCode: String(1000 + t),
            hinmei: HINMEI_POOL[seed0 % HINMEI_POOL.length]!,
            history,
            previous,
            current,
            changed: previous !== null && current !== null && previous !== current,
            changeCount,
          })
        }
      }
    }
  }
  return rows
}

