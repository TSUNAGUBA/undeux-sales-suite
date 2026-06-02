/**
 * 回帰分析の純粋計算ロジック（単回帰・重回帰の最小二乗法）。
 *
 * バックエンドは集計素材（週次系列・型番別指標）のみ返し、回帰係数・予測・決定係数は
 * ここで算出する（ランキングの順位・複合スコアと同じ「表示射影」の思想。Vue 非依存の純粋関数）。
 */

/** 2次元の点。 */
export interface Point {
  x: number
  y: number
}

/** 単回帰の結果。 */
export interface SimpleRegression {
  /** 傾き（説明変数1単位あたりの目的変数の変化）。 */
  slope: number
  /** 切片。 */
  intercept: number
  /** 決定係数 R²（0..1。説明力）。 */
  r2: number
  /** ピアソン相関係数 r（-1..1）。 */
  r: number
  /** 標本数。 */
  n: number
}

/** 単回帰（最小二乗法）。点が2未満、または x が一定で傾きが定義できない場合は null。 */
export function simpleLinearRegression(points: readonly Point[]): SimpleRegression | null {
  const n = points.length
  if (n < 2) return null

  let sumX = 0
  let sumY = 0
  for (const p of points) {
    sumX += p.x
    sumY += p.y
  }
  const meanX = sumX / n
  const meanY = sumY / n

  let sxx = 0
  let syy = 0
  let sxy = 0
  for (const p of points) {
    const dx = p.x - meanX
    const dy = p.y - meanY
    sxx += dx * dx
    syy += dy * dy
    sxy += dx * dy
  }

  if (sxx === 0) return null // x が一定 → 傾き未定義

  const slope = sxy / sxx
  const intercept = meanY - slope * meanX
  const r = syy === 0 ? 0 : sxy / Math.sqrt(sxx * syy)
  const r2 = r * r
  return { slope, intercept, r2, r, n }
}

/** 重回帰の結果。 */
export interface MultipleRegression {
  /** 係数（先頭が切片、以降が各説明変数の偏回帰係数）。長さ = 説明変数数 + 1。 */
  coefficients: number[]
  /** 決定係数 R²。 */
  r2: number
  /** 標本数。 */
  n: number
}

/** 重回帰用の1観測（説明変数ベクトルと目的変数）。 */
export interface Observation {
  features: number[]
  target: number
}

/**
 * 重回帰（最小二乗法、正規方程式 (XᵀX)b = Xᵀy を Gauss 消去で解く）。
 * 観測数が説明変数数+1 未満、または正規方程式が特異（多重共線性等）な場合は null。
 */
export function multipleLinearRegression(
  observations: readonly Observation[],
): MultipleRegression | null {
  const n = observations.length
  if (n === 0) return null
  const k = observations[0]!.features.length + 1 // +1 = 切片
  if (n < k) return null
  if (observations.some((o) => o.features.length !== k - 1)) return null

  // 計画行列（先頭列=1）。XᵀX（k×k）と Xᵀy（k）を構築する。
  const xtx: number[][] = Array.from({ length: k }, () => new Array(k).fill(0))
  const xty: number[] = new Array(k).fill(0)

  for (const obs of observations) {
    const row = [1, ...obs.features]
    for (let i = 0; i < k; i++) {
      xty[i]! += row[i]! * obs.target
      for (let j = 0; j < k; j++) {
        xtx[i]![j]! += row[i]! * row[j]!
      }
    }
  }

  const coefficients = solveLinearSystem(xtx, xty)
  if (!coefficients) return null

  // R² = 1 − SS_res / SS_tot。
  let sumY = 0
  for (const o of observations) sumY += o.target
  const meanY = sumY / n
  let ssRes = 0
  let ssTot = 0
  for (const o of observations) {
    const predicted = predict(coefficients, o.features)
    ssRes += (o.target - predicted) ** 2
    ssTot += (o.target - meanY) ** 2
  }
  const r2 = ssTot === 0 ? 0 : 1 - ssRes / ssTot
  return { coefficients, r2, n }
}

/** 係数（先頭=切片）と説明変数ベクトルから予測値を返す。 */
export function predict(coefficients: readonly number[], features: readonly number[]): number {
  let y = coefficients[0] ?? 0
  for (let i = 0; i < features.length; i++) {
    y += (coefficients[i + 1] ?? 0) * features[i]!
  }
  return y
}

/**
 * 連立一次方程式 A x = b を部分ピボット付き Gauss 消去で解く。
 * 特異（ピボットがほぼ 0）な場合は null。A は破壊的に変更しないようコピーして解く。
 */
function solveLinearSystem(a: readonly number[][], b: readonly number[]): number[] | null {
  const n = b.length
  const m = a.map((row) => [...row])
  const rhs = [...b]

  for (let col = 0; col < n; col++) {
    // 部分ピボット選択（絶対値最大の行）。
    let pivotRow = col
    let pivotAbs = Math.abs(m[col]![col]!)
    for (let r = col + 1; r < n; r++) {
      const v = Math.abs(m[r]![col]!)
      if (v > pivotAbs) {
        pivotAbs = v
        pivotRow = r
      }
    }
    if (pivotAbs < 1e-12) return null // 特異

    if (pivotRow !== col) {
      ;[m[col], m[pivotRow]] = [m[pivotRow]!, m[col]!]
      ;[rhs[col], rhs[pivotRow]] = [rhs[pivotRow]!, rhs[col]!]
    }

    const pivot = m[col]![col]!
    for (let r = 0; r < n; r++) {
      if (r === col) continue
      const factor = m[r]![col]! / pivot
      if (factor === 0) continue
      for (let c = col; c < n; c++) {
        m[r]![c]! -= factor * m[col]![c]!
      }
      rhs[r]! -= factor * rhs[col]!
    }
  }

  const x = new Array(n).fill(0)
  for (let i = 0; i < n; i++) {
    x[i] = rhs[i]! / m[i]![i]!
  }
  return x
}
