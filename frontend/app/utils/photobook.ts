/**
 * 写真帳（投入企画書レイアウト）の項目定義と Excel 出力ロジック（純粋関数）。
 *
 * 参照レイアウト（Miro の投入企画書）に倣い、商品を「品番・品名・PB・素材・下代・上代・
 * 投入予定日・投入日・PB検査・サイズ／カラー・股上/股下/裾巾・FOB・受注日・先付・伝発」等の
 * スペックシートで表現する。商品マスタに存在しない項目は空白で表示／出力する。
 *
 * 画面カードと Excel 出力で同一の項目定義を共有し、表現の一貫性を保つ（DRY）。
 */
import type { MasterProductSummary } from '~/types/api'
import { formatCurrency, formatNumber } from './format'

/** スペック項目1件（ラベルと値。値が空文字＝データなし＝空白表示）。 */
export interface PhotobookField {
  label: string
  value: string
}

/** 上代（定価）の表示（範囲。未登録は空）。 */
export function photobookPriceLabel(p: MasterProductSummary): string {
  const min = p.minSalesPrice
  const max = p.maxSalesPrice
  if (min === null && max === null) return ''
  if (min !== null && max !== null && min !== max) {
    return `${formatCurrency(min)} 〜 ${formatCurrency(max)}`
  }
  return formatCurrency(max ?? min ?? 0)
}

/**
 * ヘッダ項目（投入企画書の上段）。
 * 商品マスタに無い項目（下代・投入予定日・投入日・減価・PB検査）は空白。
 * 品番=品番CD、素材=商品記号、PB=ブランド にマッピングする。
 */
export function photobookHeaderFields(p: MasterProductSummary): PhotobookField[] {
  return [
    { label: '品番', value: p.productTypeCrd },
    { label: '品名', value: p.productName },
    { label: 'PB', value: p.brand ?? '' },
    { label: '素材', value: p.productSign },
    { label: '下代', value: '' },
    { label: '上代', value: photobookPriceLabel(p) },
    { label: '投入予定日', value: '' },
    { label: '投入日', value: '' },
    { label: '減価(C/T)', value: '' },
    { label: 'PB検査', value: '' },
  ]
}

/** 属性・実績（商品マスタから取得できる補助情報）。 */
export function photobookStatsFields(p: MasterProductSummary): PhotobookField[] {
  return [
    { label: '部門', value: p.divisionName || String(p.divisionCd) },
    { label: '季節', value: p.kisetsu || '' },
    { label: '色数', value: formatNumber(p.colorCount) },
    { label: 'サイズ数', value: formatNumber(p.sizeCount) },
    { label: 'SKU数', value: formatNumber(p.skuCount) },
    { label: '店頭在庫', value: formatNumber(p.storeStock) },
    { label: '売上数量', value: formatNumber(p.salesQuantity) },
  ]
}

/** フッタ項目（投入企画書の下段）。いずれも商品マスタに無いため空白。 */
export function photobookFooterFields(): PhotobookField[] {
  return [
    { label: '初回各', value: '' },
    { label: '股上', value: '' },
    { label: '股下', value: '' },
    { label: '裾巾', value: '' },
    { label: 'FOB', value: '' },
    { label: '受注日', value: '' },
    { label: '先付', value: '' },
    { label: '伝発', value: '' },
  ]
}

/** HTML 特殊文字をエスケープする（自己 XSS 防止）。 */
/** XML テキストのエスケープ（要素テキスト用。& < > を実体参照へ）。 */
function escapeXml(value: string): string {
  return value.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
}

/** 0 始まりの列番号を A1 形式の列文字（A, B, ... Z, AA ...）へ変換する。 */
function columnRef(index: number): string {
  let n = index + 1
  let ref = ''
  while (n > 0) {
    const rem = (n - 1) % 26
    ref = String.fromCharCode(65 + rem) + ref
    n = Math.floor((n - 1) / 26)
  }
  return ref
}

/** CRC-32（ZIP のファイル整合チェック用）。 */
function crc32(bytes: Uint8Array): number {
  let crc = ~0
  for (let i = 0; i < bytes.length; i++) {
    crc ^= bytes[i]!
    for (let j = 0; j < 8; j++) crc = (crc >>> 1) ^ (0xedb88320 & -(crc & 1))
  }
  return (~crc) >>> 0
}

/**
 * 無圧縮（store）ZIP を組み立てる（追加ライブラリ不要）。xlsx は ZIP コンテナのため、これで .xlsx を作る。
 * ファイル内容は小さな XML のみ（画像は含めない）ため store で十分。
 */
function zipStore(files: readonly { name: string; data: Uint8Array }[]): Uint8Array<ArrayBuffer> {
  const enc = new TextEncoder()
  const local: number[] = []
  const central: number[] = []
  const u16 = (arr: number[], v: number): void => { arr.push(v & 0xff, (v >>> 8) & 0xff) }
  const u32 = (arr: number[], v: number): void => {
    arr.push(v & 0xff, (v >>> 8) & 0xff, (v >>> 16) & 0xff, (v >>> 24) & 0xff)
  }

  for (const file of files) {
    const nameBytes = enc.encode(file.name)
    const crc = crc32(file.data)
    const offset = local.length
    // ローカルファイルヘッダ（署名 0x04034b50・version20・flags0・method0(store)・時刻0・日付1980-01-01）。
    u32(local, 0x04034b50); u16(local, 20); u16(local, 0); u16(local, 0); u16(local, 0); u16(local, 0x21)
    u32(local, crc); u32(local, file.data.length); u32(local, file.data.length)
    u16(local, nameBytes.length); u16(local, 0)
    for (const b of nameBytes) local.push(b)
    for (const b of file.data) local.push(b)
    // セントラルディレクトリヘッダ（署名 0x02014b50）。
    u32(central, 0x02014b50); u16(central, 20); u16(central, 20); u16(central, 0); u16(central, 0)
    u16(central, 0); u16(central, 0x21)
    u32(central, crc); u32(central, file.data.length); u32(central, file.data.length)
    u16(central, nameBytes.length); u16(central, 0); u16(central, 0); u16(central, 0); u16(central, 0)
    u32(central, 0); u32(central, offset)
    for (const b of nameBytes) central.push(b)
  }

  const out: number[] = [...local, ...central]
  const cdOffset = local.length
  const cdSize = central.length
  // EOCD（署名 0x06054b50）。
  u32(out, 0x06054b50); u16(out, 0); u16(out, 0); u16(out, files.length); u16(out, files.length)
  u32(out, cdSize); u32(out, cdOffset); u16(out, 0)
  return Uint8Array.from(out)
}

/**
 * 行列（string[][]）から最小構成の xlsx（OOXML）バイト列を生成する。追加ライブラリ不要。
 * すべてのセルはインライン文字列（t="inlineStr"）で出力する（スペックシート用途では十分）。
 */
export function buildXlsx(grid: readonly (readonly string[])[], sheetName: string): Uint8Array<ArrayBuffer> {
  const enc = new TextEncoder()
  const rowsXml = grid
    .map((row, r) => {
      const cells = row
        .map((val, c) => `<c r="${columnRef(c)}${r + 1}" t="inlineStr"><is><t xml:space="preserve">${escapeXml(val)}</t></is></c>`)
        .join('')
      return `<row r="${r + 1}">${cells}</row>`
    })
    .join('')
  const sheetXml = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>${rowsXml}</sheetData></worksheet>`
  const contentTypes = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>`
  const rootRels = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>`
  const workbook = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="${escapeXml(sheetName)}" sheetId="1" r:id="rId1"/></sheets></workbook>`
  const workbookRels = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>`

  return zipStore([
    { name: '[Content_Types].xml', data: enc.encode(contentTypes) },
    { name: '_rels/.rels', data: enc.encode(rootRels) },
    { name: 'xl/workbook.xml', data: enc.encode(workbook) },
    { name: 'xl/_rels/workbook.xml.rels', data: enc.encode(workbookRels) },
    { name: 'xl/worksheets/sheet1.xml', data: enc.encode(sheetXml) },
  ])
}

/**
 * 選択商品（1〜4件）を列・スペック項目を行に並べたグリッドを組み立てる。不足項目は空欄。
 * 画像は xlsx へ埋め込まず、参照用に URL を1行として出力する。
 */
export function buildPhotobookGrid(products: readonly MasterProductSummary[]): string[][] {
  const grid: string[][] = []
  grid.push(['項目', ...products.map((_, i) => `商品${i + 1}`)])
  for (const f of photobookHeaderFields(products[0]!)) {
    grid.push([f.label, ...products.map((p) => photobookHeaderFields(p).find((x) => x.label === f.label)?.value ?? '')])
  }
  grid.push(['画像URL', ...products.map((p) => p.primaryImageUrl ?? '')])
  for (const f of photobookStatsFields(products[0]!)) {
    grid.push([f.label, ...products.map((p) => photobookStatsFields(p).find((x) => x.label === f.label)?.value ?? '')])
  }
  for (const f of photobookFooterFields()) {
    grid.push([f.label, ...products.map(() => '')])
  }
  return grid
}

/**
 * 選択商品を実体のある xlsx ファイルとしてダウンロードする（追加ライブラリ不要）。クライアント専用。
 * 以前は HTML テーブルを .xls として保存していたが、近年の Excel が「形式/拡張子不一致」で開けないため、
 * 正規の OOXML（xlsx）で生成する。
 */
export function downloadPhotobookExcel(products: readonly MasterProductSummary[], fileName: string): void {
  if (import.meta.server || products.length === 0) return
  const bytes = buildXlsx(buildPhotobookGrid(products), '写真帳')
  const blob = new Blob([bytes], {
    type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
  })
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = fileName
  document.body.appendChild(a)
  a.click()
  document.body.removeChild(a)
  URL.revokeObjectURL(url)
}
