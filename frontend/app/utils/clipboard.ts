/**
 * クリップボード書込の共通ユーティリティ。
 * クロス集計テーブルと在庫アクション明細（滞留・不動リスト）の Excel 貼り付け用コピーで共有する。
 */

/** execCommand によるフォールバックコピー（Clipboard API 非対応・非セキュアコンテキスト用）。 */
function fallbackCopy(html: string): boolean {
  const container = document.createElement('div')
  container.setAttribute('contenteditable', 'true')
  container.style.position = 'fixed'
  container.style.left = '-9999px'
  container.style.top = '0'
  container.innerHTML = html
  document.body.appendChild(container)

  const selection = window.getSelection()
  // ユーザーが既に持っている選択範囲を退避し、コピー後に復元する。
  const savedRanges: Range[] = selection
    ? Array.from({ length: selection.rangeCount }, (_, i) => selection.getRangeAt(i))
    : []
  const range = document.createRange()
  range.selectNodeContents(container)
  selection?.removeAllRanges()
  selection?.addRange(range)

  let ok = false
  try {
    ok = document.execCommand('copy')
  } catch {
    ok = false
  }
  selection?.removeAllRanges()
  for (const saved of savedRanges) selection?.addRange(saved)
  document.body.removeChild(container)
  return ok
}

/**
 * HTML（text/html）とプレーンテキスト（text/plain）の両形式でクリップボードへコピーする。
 * Clipboard API が使えない場合（権限・非対応・非セキュアコンテキスト）は execCommand へ
 * フォールバックする。戻り値は成否（呼び出し側がフィードバック表示を行う）。
 *
 * **契約: `html` はエスケープ済み（または信頼済み DOM 由来）であること。**
 * フォールバック経路で一時要素の innerHTML に挿入されるため、生の外部データを
 * 渡すと自己 XSS の温床になる。ユーザーデータを含める場合は呼び出し側で
 * 必ず HTML エスケープしてから渡すこと。
 */
export async function copyHtmlToClipboard(html: string, text: string): Promise<boolean> {
  let ok = false
  try {
    if (navigator.clipboard?.write && typeof ClipboardItem !== 'undefined') {
      await navigator.clipboard.write([
        new ClipboardItem({
          'text/html': new Blob([html], { type: 'text/html' }),
          'text/plain': new Blob([text], { type: 'text/plain' }),
        }),
      ])
      ok = true
    }
  } catch {
    ok = false
  }
  // 主要パスが失敗（権限・非対応・非セキュア）したら execCommand へフォールバック。
  if (!ok) ok = fallbackCopy(html)
  return ok
}
