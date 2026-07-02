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
function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
}

const XLS_TD = 'border:1px solid #999;padding:3px 6px;vertical-align:top'
const XLS_TH = `${XLS_TD};background-color:#eef;font-weight:bold;text-align:left`

/**
 * 選択商品（1〜4件）を列に、スペック項目を行に並べた Excel 用 HTML テーブルを生成する。
 * 画像は best-effort で <img>（環境により表示されない場合がある）。不足項目は空欄。
 */
export function buildPhotobookExcelHtml(products: readonly MasterProductSummary[]): string {
  const rows: string[] = []

  // 見出し行（項目 / 商品1..N）。
  const headerCells = ['項目', ...products.map((_, i) => `商品${i + 1}`)]
  rows.push(`<tr>${headerCells.map((h) => `<th style="${XLS_TH}">${escapeHtml(h)}</th>`).join('')}</tr>`)

  const pushRow = (label: string, values: string[]): void => {
    const cells = [
      `<th style="${XLS_TH}">${escapeHtml(label)}</th>`,
      ...values.map((v) => `<td style="${XLS_TD}">${escapeHtml(v)}</td>`),
    ]
    rows.push(`<tr>${cells.join('')}</tr>`)
  }

  // ヘッダ項目 → 画像 → 属性 → フッタ項目 の順で1シートにまとめる。
  for (const f of photobookHeaderFields(products[0]!)) {
    pushRow(f.label, products.map((p) => photobookHeaderFields(p).find((x) => x.label === f.label)?.value ?? ''))
  }

  // 画像行（best-effort。URL が無ければ空欄）。
  const imageCells = [
    `<th style="${XLS_TH}">画像</th>`,
    ...products.map((p) =>
      p.primaryImageUrl
        ? `<td style="${XLS_TD}"><img src="${escapeHtml(p.primaryImageUrl)}" style="max-width:120px;max-height:120px" /></td>`
        : `<td style="${XLS_TD}"></td>`,
    ),
  ]
  rows.push(`<tr>${imageCells.join('')}</tr>`)

  for (const f of photobookStatsFields(products[0]!)) {
    pushRow(f.label, products.map((p) => photobookStatsFields(p).find((x) => x.label === f.label)?.value ?? ''))
  }
  for (const f of photobookFooterFields()) {
    pushRow(f.label, products.map(() => ''))
  }

  return `<table style="border-collapse:collapse;font-family:sans-serif;font-size:12px">${rows.join('')}</table>`
}

/**
 * 生成した HTML テーブルを Excel 互換ファイル（.xls）としてダウンロードする。
 * 追加ライブラリ不要（HTML テーブルを Excel が読み込める形式で保存する）。クライアント専用。
 */
export function downloadPhotobookExcel(products: readonly MasterProductSummary[], fileName: string): void {
  if (import.meta.server) return
  const table = buildPhotobookExcelHtml(products)
  const html = `<html xmlns:o="urn:schemas-microsoft-com:office:office" xmlns:x="urn:schemas-microsoft-com:office:excel" xmlns="http://www.w3.org/TR/REC-html40"><head><meta charset="utf-8" /></head><body>${table}</body></html>`
  // BOM を付けて日本語の文字化けを防ぐ。
  const blob = new Blob(['﻿', html], { type: 'application/vnd.ms-excel' })
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = fileName
  document.body.appendChild(a)
  a.click()
  document.body.removeChild(a)
  URL.revokeObjectURL(url)
}
