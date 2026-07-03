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

// ============================================================
// 画像の埋め込み（xl/media + xl/drawings）。ライブラリ非依存で PNG/JPEG を貼り付ける。
// ============================================================

/** 埋め込む画像1件（どのブロックか・拡張子・バイト列・元寸法[px]）。 */
export interface EmbeddedImage {
  block: number
  ext: 'png' | 'jpeg'
  data: Uint8Array
  width: number
  height: number
}

/**
 * PNG/JPEG のバイト列から拡張子と寸法（px）を判定する。判別不能・未対応形式は null。
 * PNG は IHDR（16..23）、JPEG は SOF マーカーから幅・高さを読む。
 */
function parseImage(bytes: Uint8Array): { ext: 'png' | 'jpeg'; width: number; height: number } | null {
  // PNG（署名 89 50 4E 47 0D 0A 1A 0A ＋ IHDR）。
  if (
    bytes.length > 24 && bytes[0] === 0x89 && bytes[1] === 0x50 && bytes[2] === 0x4e && bytes[3] === 0x47
  ) {
    const width = ((bytes[16]! << 24) | (bytes[17]! << 16) | (bytes[18]! << 8) | bytes[19]!) >>> 0
    const height = ((bytes[20]! << 24) | (bytes[21]! << 16) | (bytes[22]! << 8) | bytes[23]!) >>> 0
    return width > 0 && height > 0 ? { ext: 'png', width, height } : null
  }
  // JPEG（署名 FF D8）。SOF0-3/5-7/9-11/13-15 の直後に高さ・幅（各2バイト）。
  if (bytes.length > 4 && bytes[0] === 0xff && bytes[1] === 0xd8) {
    let offset = 2
    while (offset + 9 < bytes.length) {
      if (bytes[offset] !== 0xff) { offset++; continue }
      const marker = bytes[offset + 1]!
      const isSof =
        (marker >= 0xc0 && marker <= 0xc3)
        || (marker >= 0xc5 && marker <= 0xc7)
        || (marker >= 0xc9 && marker <= 0xcb)
        || (marker >= 0xcd && marker <= 0xcf)
      if (isSof) {
        const height = (bytes[offset + 5]! << 8) | bytes[offset + 6]!
        const width = (bytes[offset + 7]! << 8) | bytes[offset + 8]!
        return width > 0 && height > 0 ? { ext: 'jpeg', width, height } : null
      }
      // スタンドアロンマーカー（SOI/EOI/RSTn）は 2 バイト、その他は長さフィールドぶんスキップ。
      if (marker === 0xd8 || marker === 0xd9 || (marker >= 0xd0 && marker <= 0xd7)) {
        offset += 2
      } else {
        offset += 2 + ((bytes[offset + 2]! << 8) | bytes[offset + 3]!)
      }
    }
    return null
  }
  return null
}

/**
 * 選択商品（最大4件）の主画像を取得し、埋め込み用に整える（クライアント専用・非ブロッキング）。
 * CORS・ネットワーク失敗や未対応形式はその商品だけ画像なしにフォールバックする（原則4）。
 */
export async function loadPhotobookImages(
  products: readonly MasterProductSummary[],
): Promise<EmbeddedImage[]> {
  if (import.meta.server) return []
  const blocks = Math.max(1, Math.min(4, products.length))
  const out: EmbeddedImage[] = []
  for (let b = 0; b < blocks; b++) {
    const url = products[b]?.primaryImageUrl
    if (!url) continue
    try {
      const res = await fetch(url)
      if (!res.ok) continue
      const buf = new Uint8Array(await res.arrayBuffer())
      const info = parseImage(buf)
      if (!info) continue
      out.push({ block: b, ext: info.ext, data: buf, width: info.width, height: info.height })
    } catch {
      // 画像は補助。取得失敗はその商品を画像なしで出力し、Excel 生成全体は止めない。
    }
  }
  return out
}

// 画像枠の実効サイズ（px）。画像行 7..21（15行×20pt）＝300pt≒400px、
// 1ブロック7列（列幅9・保守的に 7px/文字＝63px/列）＝441px。枠内に収まるよう保守的に見積もる。
const IMAGE_FRAME_W_PX = 441
const IMAGE_FRAME_H_PX = 400
const EMU_PER_PX = 9525

/** 画像埋め込みの drawing パーツ（drawing1.xml・その rels・media バイト列）を生成する。 */
function buildDrawingParts(images: readonly EmbeddedImage[]): {
  drawingXml: string
  drawingRels: string
  media: { name: string; data: Uint8Array }[]
} {
  const anchors: string[] = []
  const rels: string[] = []
  const media: { name: string; data: Uint8Array }[] = []
  images.forEach((img, i) => {
    const n = i + 1
    const rid = `rId${n}`
    // 枠内に収まるよう高さ優先で拡縮（高さ基準・幅超過時は幅で抑える＝contain）。
    const scale = Math.min(IMAGE_FRAME_H_PX / img.height, IMAGE_FRAME_W_PX / img.width)
    const cx = Math.round(img.width * scale * EMU_PER_PX)
    const cy = Math.round(img.height * scale * EMU_PER_PX)
    const col = img.block * BLOCK_COLS
    anchors.push(
      '<xdr:oneCellAnchor>'
      + `<xdr:from><xdr:col>${col}</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>7</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from>`
      + `<xdr:ext cx="${cx}" cy="${cy}"/>`
      + '<xdr:pic>'
      + `<xdr:nvPicPr><xdr:cNvPr id="${n}" name="商品画像${n}" descr=""/><xdr:cNvPicPr><a:picLocks noChangeAspect="1"/></xdr:cNvPicPr></xdr:nvPicPr>`
      + `<xdr:blipFill><a:blip r:embed="${rid}"/><a:stretch><a:fillRect/></a:stretch></xdr:blipFill>`
      + `<xdr:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="${cx}" cy="${cy}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></xdr:spPr>`
      + '</xdr:pic>'
      + '<xdr:clientData/>'
      + '</xdr:oneCellAnchor>',
    )
    rels.push(
      `<Relationship Id="${rid}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="../media/image${n}.${img.ext}"/>`,
    )
    media.push({ name: `xl/media/image${n}.${img.ext}`, data: img.data })
  })
  const drawingXml = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n<xdr:wsDr xmlns:xdr="http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">${anchors.join('')}</xdr:wsDr>`
  const drawingRels = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">${rels.join('')}</Relationships>`
  return { drawingXml, drawingRels, media }
}

// ============================================================
// 投入企画書レイアウトの xlsx 生成（結合セル・罫線・SUM 数式対応）
// 参照レイアウト（Miro の投入企画書）に合わせ、同一構造の縦ブロックを横に最大4つ並べる。
// 1ブロック=7列（ブロック1:A-G / 2:H-N / 3:O-U / 4:V-AB）。1行目はタイトルを全ブロックで結合。
// ============================================================

/** 罫線の太さ。thin=細実線（薄グレー）/ medium=太実線（黒）/ none=無し。 */
type BorderWeight = 'none' | 'thin' | 'medium'

/** セルの内容とスタイル指定（罫線は位置から自動算出）。 */
interface CellMeta {
  value?: string
  /** 数式（先頭の = は付けない。例 "SUM(B28:F28)"）。 */
  formula?: string
  /** フォント: 0=標準 / 1=太字（ラベル）/ 2=タイトル（太字・大）。 */
  fontId?: number
  align?: 'center' | 'left'
  wrap?: boolean
}

/** 各セクションの下端行（0始まり）。この行の下辺と、次行の上辺を太線にする。 */
const THICK_BOTTOM_ROWS = new Set([0, 6, 21, 25, 31, 34])
const LAST_ROW = 34
const BLOCK_COLS = 7

/**
 * 選択商品（1〜4件）から投入企画書レイアウトの xlsx バイト列を生成する（追加ライブラリ不要）。
 * すべての値はインライン文字列、「計」は SUM 数式（Excel が開封時に再計算）。
 */
export function buildPhotobookWorkbook(
  products: readonly MasterProductSummary[],
  images: readonly EmbeddedImage[] = [],
): Uint8Array<ArrayBuffer> {
  const enc = new TextEncoder()
  const blocks = Math.max(1, Math.min(4, products.length))
  const lastCol = blocks * BLOCK_COLS - 1

  const meta = new Map<string, CellMeta>()
  const merges: string[] = []
  const rowHeights = new Map<number, number>()

  const cellRef = (r: number, c: number): string => `${columnRef(c)}${r + 1}`
  const place = (r: number, c: number, m: CellMeta): void => { meta.set(`${r}:${c}`, m) }
  const mergeRange = (r1: number, c1: number, r2: number, c2: number, m: CellMeta): void => {
    merges.push(`${cellRef(r1, c1)}:${cellRef(r2, c2)}`)
    place(r1, c1, m)
  }

  // タイトル（全ブロックを跨いで結合。編集可能な状態）。
  mergeRange(0, 0, 0, lastCol, { value: '写真帳', fontId: 2, align: 'center' })
  rowHeights.set(1, 28) // 品名の折返し用に少し高く
  // 画像枠（行 7..21）は固定高さにして、貼付画像の枠サイズ（IMAGE_FRAME_H_PX）と一致させる。
  for (let r = 7; r <= 21; r++) rowHeights.set(r, 20)

  for (let b = 0; b < blocks; b++) {
    const p = products[b]!
    const o = b * BLOCK_COLS // 列オフセット

    // 1) 基本情報（label|value(2)|label|value(3) を6行）。
    const basic: [string, string, string, string][] = [
      ['品番', p.productTypeCrd, '品名', p.productName],
      ['PB', p.brand ?? '', '素材', p.productSign],
      ['下代', '', '上代', photobookPriceLabel(p)],
      ['投入予定日', '', '納品日', ''],
      ['成海(O/T)', '', '投入日', ''],
      ['PB検査', '', '', ''],
    ]
    basic.forEach((row, i) => {
      const r = 1 + i
      place(r, o + 0, { value: row[0], fontId: 1 })
      mergeRange(r, o + 1, r, o + 2, { value: row[1], wrap: i === 0 })
      place(r, o + 3, { value: row[2], fontId: 1 })
      mergeRange(r, o + 4, r, o + 6, { value: row[3], wrap: i === 0 })
    })

    // 2) 商品画像スペース（7列×15行を結合・中央揃え。画像を貼り付ける想定の空欄）。
    mergeRange(7, o + 0, 21, o + 6, { value: '', align: 'center' })

    // 3) カラー・備考（ラベル行＋3行の結合入力欄）。
    mergeRange(22, o + 0, 22, o + 6, { value: '（カラー）', fontId: 1, align: 'left' })
    mergeRange(23, o + 0, 25, o + 6, { value: '', align: 'left' })

    // 4) サイズ・数量マトリクス（カラー×サイズ。右端と最下段に SUM の「計」）。
    place(26, o + 0, { value: 'カラー＼サイズ', fontId: 1 })
    place(26, o + 6, { value: '計', fontId: 1 })
    for (let dr = 0; dr < 4; dr++) {
      const r = 27 + dr
      const excelRow = r + 1
      place(r, o + 0, { value: '' }) // カラー名（空欄）
      // 数量セル（o+1..o+5）は空欄（一般ループで罫線付き空セルとして出力）。
      place(r, o + 6, { formula: `SUM(${columnRef(o + 1)}${excelRow}:${columnRef(o + 5)}${excelRow})` })
    }
    // 最下段「計」（各列合計＋右下に総合計）。
    place(31, o + 0, { value: '計', fontId: 1 })
    for (let sc = 0; sc < 5; sc++) {
      const col = o + 1 + sc
      place(31, col, { formula: `SUM(${columnRef(col)}28:${columnRef(col)}31)` })
    }
    place(31, o + 6, { formula: `SUM(${columnRef(o + 1)}32:${columnRef(o + 5)}32)` })

    // 5) 寸法・管理（左:股上/股下/裾巾 ／ 中央:FOB ／ 右:受注日/先付/伝発）。
    const dims: [string, string][] = [['股上', '受注日'], ['股下', '先付'], ['裾巾', '伝発']]
    dims.forEach((d, i) => {
      const r = 32 + i
      place(r, o + 0, { value: d[0], fontId: 1 })
      mergeRange(r, o + 1, r, o + 2, { value: '' })
      place(r, o + 4, { value: d[1], fontId: 1 })
      mergeRange(r, o + 5, r, o + 6, { value: '' })
    })
    mergeRange(32, o + 3, 34, o + 3, { value: 'FOB', fontId: 1, align: 'center' })
  }

  // ---- スタイルレジストリ（罫線・書式を重複排除して index 化） ----
  const borders: string[] = []
  const borderIndex = new Map<string, number>()
  const side = (name: string, w: BorderWeight): string => {
    if (w === 'none') return `<${name}/>`
    const color = w === 'medium' ? 'FF000000' : 'FFBFBFBF'
    return `<${name} style="${w}"><color rgb="${color}"/></${name}>`
  }
  const regBorder = (l: BorderWeight, r: BorderWeight, t: BorderWeight, b: BorderWeight): number => {
    const key = `${l}|${r}|${t}|${b}`
    const existing = borderIndex.get(key)
    if (existing !== undefined) return existing
    const idx = borders.length
    borders.push(`<border>${side('left', l)}${side('right', r)}${side('top', t)}${side('bottom', b)}<diagonal/></border>`)
    borderIndex.set(key, idx)
    return idx
  }
  const xfs: string[] = []
  const xfIndex = new Map<string, number>()
  const regXf = (fontId: number, borderId: number, align: 'center' | 'left', wrap: boolean): number => {
    const key = `${fontId}|${borderId}|${align}|${wrap}`
    const existing = xfIndex.get(key)
    if (existing !== undefined) return existing
    const idx = xfs.length
    xfs.push(
      `<xf numFmtId="0" fontId="${fontId}" fillId="0" borderId="${borderId}" xfId="0" applyFont="1" applyBorder="1" applyAlignment="1"><alignment horizontal="${align}" vertical="center"${wrap ? ' wrapText="1"' : ''}/></xf>`,
    )
    xfIndex.set(key, idx)
    return idx
  }
  regBorder('none', 'none', 'none', 'none') // borderId 0（既定・cellStyleXfs 用）
  regXf(0, 0, 'center', false) // xf 0（既定）

  // ---- sheetData（全セルを罫線付きで出力。値/数式のあるセルは内容も） ----
  let sheetData = ''
  for (let r = 0; r <= LAST_ROW; r++) {
    let rowCells = ''
    for (let c = 0; c <= lastCol; c++) {
      const localCol = c % BLOCK_COLS
      const left: BorderWeight = localCol === 0 ? 'medium' : 'thin'
      const right: BorderWeight = localCol === BLOCK_COLS - 1 ? 'medium' : 'thin'
      const top: BorderWeight = r === 0 || THICK_BOTTOM_ROWS.has(r - 1) ? 'medium' : 'thin'
      const bottom: BorderWeight = THICK_BOTTOM_ROWS.has(r) ? 'medium' : 'thin'
      const borderId = regBorder(left, right, top, bottom)
      const m = meta.get(`${r}:${c}`)
      const s = regXf(m?.fontId ?? 0, borderId, m?.align ?? 'center', m?.wrap ?? false)
      const ref = cellRef(r, c)
      if (m?.formula) {
        rowCells += `<c r="${ref}" s="${s}"><f>${escapeXml(m.formula)}</f></c>`
      } else if (m?.value) {
        rowCells += `<c r="${ref}" s="${s}" t="inlineStr"><is><t xml:space="preserve">${escapeXml(m.value)}</t></is></c>`
      } else {
        rowCells += `<c r="${ref}" s="${s}"/>`
      }
    }
    const ht = rowHeights.get(r)
    sheetData += `<row r="${r + 1}"${ht ? ` ht="${ht}" customHeight="1"` : ''}>${rowCells}</row>`
  }

  const mergeXml = `<mergeCells count="${merges.length}">${merges.map((mc) => `<mergeCell ref="${mc}"/>`).join('')}</mergeCells>`
  const colsXml = `<cols><col min="1" max="${lastCol + 1}" width="9" customWidth="1"/></cols>`
  // 画像がある場合のみ drawing 参照（と r 名前空間）を worksheet に追加する（順序: mergeCells の後）。
  const hasImages = images.length > 0
  const rNs = hasImages ? ' xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"' : ''
  const drawingTag = hasImages ? '<drawing r:id="rId1"/>' : ''
  const sheetXml = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"${rNs}><dimension ref="A1:${cellRef(LAST_ROW, lastCol)}"/>${colsXml}<sheetData>${sheetData}</sheetData>${mergeXml}${drawingTag}</worksheet>`

  // ゴシック体（Meiryo・charset=128・family=3=modern）。0=標準/1=太字/2=タイトル。
  const fontXml = (bold: boolean, size: number): string =>
    `<font>${bold ? '<b/>' : ''}<sz val="${size}"/><color rgb="FF000000"/><name val="Meiryo"/><family val="3"/><charset val="128"/></font>`
  const stylesXml = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="3">${fontXml(false, 11)}${fontXml(true, 11)}${fontXml(true, 14)}</fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="${borders.length}">${borders.join('')}</borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="${xfs.length}">${xfs.join('')}</cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>`

  // 画像がある場合、拡張子ごとの Default と drawing の Override、シートの rels を足す。
  const imageExts = [...new Set(images.map((i) => i.ext))]
  const imageDefaults = imageExts
    .map((e) => `<Default Extension="${e}" ContentType="image/${e}"/>`)
    .join('')
  const drawingOverride = hasImages
    ? '<Override PartName="/xl/drawings/drawing1.xml" ContentType="application/vnd.openxmlformats-officedocument.drawing+xml"/>'
    : ''
  const contentTypes = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/>${imageDefaults}<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>${drawingOverride}</Types>`
  const rootRels = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>`
  // fullCalcOnLoad=1 で開封時に SUM を再計算させる。
  const workbook = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="写真帳" sheetId="1" r:id="rId1"/></sheets><calcPr calcId="0" fullCalcOnLoad="1"/></workbook>`
  const workbookRels = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>`

  const parts: { name: string; data: Uint8Array }[] = [
    { name: '[Content_Types].xml', data: enc.encode(contentTypes) },
    { name: '_rels/.rels', data: enc.encode(rootRels) },
    { name: 'xl/workbook.xml', data: enc.encode(workbook) },
    { name: 'xl/_rels/workbook.xml.rels', data: enc.encode(workbookRels) },
    { name: 'xl/styles.xml', data: enc.encode(stylesXml) },
    { name: 'xl/worksheets/sheet1.xml', data: enc.encode(sheetXml) },
  ]
  if (hasImages) {
    const { drawingXml, drawingRels, media } = buildDrawingParts(images)
    const sheetRels = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing" Target="../drawings/drawing1.xml"/></Relationships>`
    parts.push(
      { name: 'xl/worksheets/_rels/sheet1.xml.rels', data: enc.encode(sheetRels) },
      { name: 'xl/drawings/drawing1.xml', data: enc.encode(drawingXml) },
      { name: 'xl/drawings/_rels/drawing1.xml.rels', data: enc.encode(drawingRels) },
      ...media,
    )
  }
  return zipStore(parts)
}

/**
 * 選択商品を投入企画書レイアウトの xlsx ファイルとしてダウンロードする（追加ライブラリ不要）。クライアント専用。
 * 主画像は非同期取得して画像枠（高さ優先で contain）に貼り付ける。取得失敗時は画像なしで出力する。
 */
export async function downloadPhotobookExcel(
  products: readonly MasterProductSummary[],
  fileName: string,
): Promise<void> {
  if (import.meta.server || products.length === 0) return
  const images = await loadPhotobookImages(products)
  const bytes = buildPhotobookWorkbook(products, images)
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
