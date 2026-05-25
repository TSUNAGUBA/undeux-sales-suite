/**
 * SKU サイズ表記の並び順ユーティリティ。
 *
 * ルール:
 *   - 数値変換可能（"100", "110", "M100" ではなく純粋な数字列）→ 数値として昇順
 *   - 文字列サイズ → 規定の順序:
 *       XS = SS < S < M < L < LL = 2L < LLL = 3L < LLLL = 4L
 *     ※ 元仕様には 'S' の記載が無いが、XS/M の間に位置する一般的サイズなので
 *       線形補完で含めている。同位（XS=SS など）の同点はサブキー（元文字列）で
 *       決定論的に並べる。
 *   - 数値と文字列が混在するケースは、数値グループを先頭に配置する。
 *   - 未知の値は末尾。
 */

const STRING_SIZE_RANK: Record<string, number> = {
  XS: 1,
  SS: 1,
  S: 2,
  M: 3,
  L: 4,
  LL: 5,
  '2L': 5,
  LLL: 6,
  '3L': 6,
  LLLL: 7,
  '4L': 7,
}

const NUMERIC_GROUP = 0
const STRING_GROUP = 1
const UNKNOWN_GROUP = 2

/**
 * 並び順の比較キーを返す。
 * [グループ, 主キー数値, サブキー文字列] の lexicographic 比較で並ぶ。
 */
export function sizeSortKey(size: string | null | undefined): [number, number, string] {
  const raw = (size ?? '').trim()
  if (raw === '') {
    return [UNKNOWN_GROUP, 0, '']
  }

  // 純粋な数字列のみ数値扱い（"M100" などは含めない）。
  if (/^\d+(\.\d+)?$/.test(raw)) {
    return [NUMERIC_GROUP, Number.parseFloat(raw), raw]
  }

  const upper = raw.toUpperCase()
  const rank = STRING_SIZE_RANK[upper]
  if (rank !== undefined) {
    return [STRING_GROUP, rank, upper]
  }

  return [UNKNOWN_GROUP, 0, upper]
}

/** 2つのサイズ表記を比較する（Array.prototype.sort 用）。 */
export function compareSize(a: string | null | undefined, b: string | null | undefined): number {
  const ka = sizeSortKey(a)
  const kb = sizeSortKey(b)
  if (ka[0] !== kb[0]) return ka[0] - kb[0]
  if (ka[1] !== kb[1]) return ka[1] - kb[1]
  return ka[2] < kb[2] ? -1 : ka[2] > kb[2] ? 1 : 0
}
